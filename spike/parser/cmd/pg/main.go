// Command pg runs the adversarial corpus through wasilibs/go-pgquery, the
// WebAssembly build of libpg_query (PostgreSQL's own parser, no cgo).
//
// It is deliberately written the way RTFQ's guard would be: an ALLOW-LIST of
// statement node types, an exhaustive tree walk (not a top-level type switch),
// and predicate analysis rather than a WHERE-presence test.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	pgquery "github.com/wasilibs/go-pgquery"
	"rtfqspike/corpus"
)

// allowedStmts is the entire set of statement node types RTFQ will execute.
// Everything else is refused. This is an allow-list on purpose: COPY ... FROM
// PROGRAM, DO, GRANT and SET are none of them "DDL", and a DDL deny-list lets
// all four through.
var allowedStmts = map[string]bool{
	"SelectStmt":  true,
	"InsertStmt":  true,
	"UpdateStmt":  true,
	"DeleteStmt":  true,
	"MergeStmt":   true,
	"ExplainStmt": true, // only without ANALYZE; checked separately
}

var mutatingStmts = map[string]bool{
	"InsertStmt": true,
	"UpdateStmt": true,
	"DeleteStmt": true,
	"MergeStmt":  true,
}

type result struct {
	verdict corpus.Verdict
	target  string
	detail  string
}

func classify(sql string) result {
	jsonStr, err := pgquery.ParseToJSON(sql)
	if err != nil {
		return result{corpus.Reject, "", "parse error: " + firstLine(err.Error())}
	}

	var tree map[string]any
	if err := json.Unmarshal([]byte(jsonStr), &tree); err != nil {
		return result{corpus.Reject, "", "json error"}
	}

	stmts, _ := tree["stmts"].([]any)
	if len(stmts) == 0 {
		return result{corpus.Reject, "", "no statement"}
	}
	if len(stmts) > 1 {
		return result{corpus.Reject, "", fmt.Sprintf("multi-statement (%d)", len(stmts))}
	}

	// Exhaustive walk: collect every statement node type anywhere in the tree,
	// so a write buried in a CTE cannot hide behind a SELECT at the top.
	found := map[string]bool{}
	var nodes []map[string]any
	walk(tree, func(key string, val map[string]any) {
		if isStmtKey(key) {
			found[key] = true
			nodes = append(nodes, map[string]any{key: val})
		}
	})

	for name := range found {
		if !allowedStmts[name] {
			return result{corpus.Reject, "", "disallowed node: " + name}
		}
	}

	// EXPLAIN ANALYZE executes the statement it explains.
	if found["ExplainStmt"] {
		if explainHasAnalyze(tree) {
			return result{corpus.Reject, "", "EXPLAIN ANALYZE executes"}
		}
	}

	// PostgreSQL SELECT INTO creates a table: DDL wearing a SelectStmt.
	if hasKeyAnywhere(tree, "intoClause") {
		return result{corpus.Reject, "", "SELECT INTO creates a relation"}
	}

	isMutation := false
	for name := range found {
		if mutatingStmts[name] {
			isMutation = true
		}
	}

	if !isMutation {
		return result{corpus.Read, firstRelation(tree), "read"}
	}

	// Qualification: UPDATE and DELETE must carry a non-trivial predicate.
	for _, n := range nodes {
		for name, raw := range n {
			if name != "UpdateStmt" && name != "DeleteStmt" {
				continue
			}
			body, _ := raw.(map[string]any)
			where, ok := body["whereClause"]
			if !ok || where == nil {
				return result{corpus.Reject, "", "unqualified " + name}
			}
			if triviallyTrue(where) {
				return result{corpus.Reject, "", "trivially-true predicate"}
			}
		}
	}

	return result{corpus.Mutation, writeTarget(nodes), "bounded mutation"}
}

// --- tree helpers --------------------------------------------------------

// isStmtKey reports whether a JSON key names a statement node type.
// Node type keys are upper-camel ("SelectStmt"); field names are lower-camel
// ("stmt", "whereClause"), so the leading capital is the discriminator.
func isStmtKey(key string) bool {
	return strings.HasSuffix(key, "Stmt") && key[0] >= 'A' && key[0] <= 'Z'
}

func walk(node any, fn func(key string, val map[string]any)) {
	switch n := node.(type) {
	case map[string]any:
		for k, v := range n {
			if child, ok := v.(map[string]any); ok {
				fn(k, child)
			}
			walk(v, fn)
		}
	case []any:
		for _, v := range n {
			walk(v, fn)
		}
	}
}

func hasKeyAnywhere(node any, key string) bool {
	found := false
	var rec func(any)
	rec = func(n any) {
		if found {
			return
		}
		switch t := n.(type) {
		case map[string]any:
			for k, v := range t {
				if k == key && v != nil {
					found = true
					return
				}
				rec(v)
			}
		case []any:
			for _, v := range t {
				rec(v)
			}
		}
	}
	rec(node)
	return found
}

func explainHasAnalyze(tree any) bool {
	analyze := false
	walk(tree, func(key string, val map[string]any) {
		if key != "DefElem" {
			return
		}
		if name, _ := val["defname"].(string); strings.EqualFold(name, "analyze") {
			analyze = true
		}
	})
	return analyze
}

// triviallyTrue implements the predicate analysis the row cap cannot replace:
// WHERE true, WHERE 1=1, and any OR-branch that reduces to either.
func triviallyTrue(node any) bool {
	m, ok := node.(map[string]any)
	if !ok {
		return false
	}

	if c, ok := m["A_Const"].(map[string]any); ok {
		if b, ok := c["boolval"].(map[string]any); ok {
			if v, ok := b["boolval"].(bool); ok && v {
				return true
			}
		}
	}

	if e, ok := m["A_Expr"].(map[string]any); ok {
		if opName(e) == "=" && sameConst(e["lexpr"], e["rexpr"]) {
			return true
		}
	}

	if b, ok := m["BoolExpr"].(map[string]any); ok {
		op, _ := b["boolop"].(string)
		args, _ := b["args"].([]any)
		switch op {
		case "OR_EXPR":
			for _, a := range args {
				if triviallyTrue(a) {
					return true
				}
			}
		case "AND_EXPR":
			if len(args) == 0 {
				return false
			}
			for _, a := range args {
				if !triviallyTrue(a) {
					return false
				}
			}
			return true
		}
	}
	return false
}

func opName(expr map[string]any) string {
	names, _ := expr["name"].([]any)
	for _, n := range names {
		if m, ok := n.(map[string]any); ok {
			if s, ok := m["String"].(map[string]any); ok {
				if v, ok := s["sval"].(string); ok {
					return v
				}
			}
		}
	}
	return ""
}

func sameConst(a, b any) bool {
	ja, err1 := json.Marshal(stripLocation(a))
	jb, err2 := json.Marshal(stripLocation(b))
	if err1 != nil || err2 != nil {
		return false
	}
	if !strings.Contains(string(ja), "A_Const") {
		return false
	}
	return string(ja) == string(jb)
}

// stripLocation removes byte offsets so two identical literals at different
// positions compare equal.
func stripLocation(node any) any {
	switch t := node.(type) {
	case map[string]any:
		out := map[string]any{}
		for k, v := range t {
			if k == "location" {
				continue
			}
			out[k] = stripLocation(v)
		}
		return out
	case []any:
		out := make([]any, 0, len(t))
		for _, v := range t {
			out = append(out, stripLocation(v))
		}
		return out
	}
	return node
}

func writeTarget(nodes []map[string]any) string {
	for _, n := range nodes {
		for name, raw := range n {
			if !mutatingStmts[name] {
				continue
			}
			body, _ := raw.(map[string]any)
			if rel, ok := body["relation"].(map[string]any); ok {
				return rangeVarName(rel)
			}
		}
	}
	return ""
}

func firstRelation(tree any) string {
	name := ""
	walk(tree, func(key string, val map[string]any) {
		if key == "RangeVar" && name == "" {
			name = rangeVarName(val)
		}
	})
	return name
}

// rangeVarName renders schema.relation. An absent schemaname is NOT "public" in
// general -- it resolves through search_path at execution time. Rendering the
// assumption here makes that dependency visible.
func rangeVarName(rv map[string]any) string {
	schema, _ := rv["schemaname"].(string)
	rel, _ := rv["relname"].(string)
	catalog, _ := rv["catalogname"].(string)
	if schema == "" {
		schema = "public" // ASSUMES pinned search_path -- see findings
	}
	if catalog != "" {
		return catalog + "." + schema + "." + rel
	}
	return schema + "." + rel
}

func firstLine(s string) string {
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return s[:i]
	}
	return s
}

// --- main ----------------------------------------------------------------

func main() {
	fmt.Println("=== PostgreSQL: wasilibs/go-pgquery (libpg_query via wasm, CGO_ENABLED=0) ===")
	fmt.Println()

	pass, fail := 0, 0
	var failures []string

	for _, c := range corpus.PG {
		got := classify(c.SQL)
		ok := got.verdict == c.Want
		// Target only matters when we allowed the statement through.
		targetOK := true
		if ok && c.Target != "" && got.target != c.Target {
			targetOK = false
		}
		mark := "PASS"
		if !ok || !targetOK {
			mark = "FAIL"
			fail++
			failures = append(failures, fmt.Sprintf("%-22s want=%-8s got=%-8s target want=%q got=%q  (%s)",
				c.Name, c.Want, got.verdict, c.Target, got.target, got.detail))
		} else {
			pass++
		}
		fmt.Printf("%s  %-22s %-8s %-28s %s\n", mark, c.Name, got.verdict, got.target, got.detail)
	}

	fmt.Println()
	fmt.Printf("RESULT: %d passed, %d failed, %d total\n", pass, fail, len(corpus.PG))
	if len(failures) > 0 {
		fmt.Println("\nFAILURES:")
		sort.Strings(failures)
		for _, f := range failures {
			fmt.Println("  " + f)
		}
	}

	// --- deparse round-trip: needed for LIMIT injection in M1 -------------
	fmt.Println("\n=== Deparse round-trip (LIMIT injection depends on this) ===")
	tree, err := pgquery.Parse("SELECT id, name FROM public.orders WHERE id > 5")
	if err != nil {
		fmt.Println("  parse failed:", err)
	} else {
		out, err := pgquery.Deparse(tree)
		if err != nil {
			fmt.Println("  deparse failed:", err)
		} else {
			fmt.Println("  deparsed:", out)
		}
	}

	// --- timing ----------------------------------------------------------
	fmt.Println("\n=== Parse latency (wasm) ===")
	bench := map[string]string{
		"simple select": "SELECT * FROM orders WHERE id = 1",
		"complex":       "WITH a AS (SELECT 1), b AS (UPDATE orders SET vip = true WHERE id = 1 RETURNING id) SELECT * FROM b JOIN a ON true",
	}
	names := make([]string, 0, len(bench))
	for k := range bench {
		names = append(names, k)
	}
	sort.Strings(names)
	for _, name := range names {
		sql := bench[name]
		// warm the wasm module first
		_, _ = pgquery.ParseToJSON(sql)
		const n = 500
		start := time.Now()
		for i := 0; i < n; i++ {
			if _, err := pgquery.ParseToJSON(sql); err != nil {
				fmt.Println("  bench error:", err)
				break
			}
		}
		fmt.Printf("  %-14s %v/parse\n", name, time.Since(start)/n)
	}

	if fail > 0 {
		os.Exit(1)
	}
}
