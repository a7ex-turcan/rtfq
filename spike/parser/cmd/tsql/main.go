// Command tsql runs the adversarial corpus through sqlc-dev/teesql, a pure-Go
// T-SQL parser whose AST mirrors Microsoft's ScriptDom.
//
// Same guard shape as the Postgres spike: allow-list of statement types,
// exhaustive walk, predicate analysis.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	"github.com/sqlc-dev/teesql/parser"
	"rtfqspike/corpus"
)

var allowedStmts = map[string]bool{
	"SelectStatement": true,
	"InsertStatement": true,
	"UpdateStatement": true,
	"DeleteStatement": true,
	"MergeStatement":  true,
}

var mutatingStmts = map[string]bool{
	"InsertStatement": true,
	"UpdateStatement": true,
	"DeleteStatement": true,
	"MergeStatement":  true,
}

type result struct {
	verdict corpus.Verdict
	target  string
	detail  string
}

func parseTree(sql string) (map[string]any, error) {
	script, err := parser.Parse(context.Background(), strings.NewReader(sql))
	if err != nil {
		return nil, err
	}
	b, err := parser.MarshalScript(script)
	if err != nil {
		return nil, err
	}
	var tree map[string]any
	if err := json.Unmarshal(b, &tree); err != nil {
		return nil, err
	}
	return tree, nil
}

func classify(sql string) result {
	tree, err := parseTree(sql)
	if err != nil {
		return result{corpus.Reject, "", "parse error: " + firstLine(err.Error())}
	}

	// Statements live under Batches[].Statements[]. GO produces extra batches,
	// and T-SQL allows several statements in one batch with no separator at
	// all, so count across every batch.
	stmts := allStatements(tree)
	if len(stmts) == 0 {
		return result{corpus.Reject, "", "no statement"}
	}
	if len(stmts) > 1 {
		return result{corpus.Reject, "", fmt.Sprintf("multi-statement (%d)", len(stmts))}
	}
	if len(batches(tree)) > 1 {
		return result{corpus.Reject, "", "multiple batches"}
	}

	// Exhaustive: a nested statement anywhere disqualifies.
	found := map[string]bool{}
	walk(tree, func(v map[string]any) {
		if t := typeOf(v); strings.HasSuffix(t, "Statement") {
			found[t] = true
		}
	})
	for name := range found {
		if !allowedStmts[name] {
			return result{corpus.Reject, "", "disallowed node: " + name}
		}
	}

	// SELECT ... INTO creates a table here too.
	if hasKeyAnywhere(tree, "Into") {
		return result{corpus.Reject, "", "SELECT INTO creates a relation"}
	}

	isMutation := false
	for name := range found {
		if mutatingStmts[name] {
			isMutation = true
		}
	}
	if !isMutation {
		return result{corpus.Read, firstTable(tree), "read"}
	}

	stmt := stmts[0]
	if found["UpdateStatement"] || found["DeleteStatement"] {
		where := findKey(stmt, "WhereClause")
		if where == nil {
			return result{corpus.Reject, "", "unqualified mutation"}
		}
		if triviallyTrue(where) {
			return result{corpus.Reject, "", "trivially-true predicate"}
		}
	}

	return result{corpus.Mutation, writeTarget(stmt), "bounded mutation"}
}

// --- tree helpers --------------------------------------------------------

func typeOf(v map[string]any) string {
	t, _ := v["$type"].(string)
	return t
}

func batches(tree map[string]any) []any {
	b, _ := tree["Batches"].([]any)
	return b
}

func allStatements(tree map[string]any) []map[string]any {
	var out []map[string]any
	for _, b := range batches(tree) {
		bm, ok := b.(map[string]any)
		if !ok {
			continue
		}
		list, _ := bm["Statements"].([]any)
		for _, s := range list {
			if sm, ok := s.(map[string]any); ok {
				out = append(out, sm)
			}
		}
	}
	return out
}

func walk(node any, fn func(map[string]any)) {
	switch n := node.(type) {
	case map[string]any:
		fn(n)
		for _, v := range n {
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
	walk(node, func(m map[string]any) {
		if v, ok := m[key]; ok && v != nil {
			found = true
		}
	})
	return found
}

func findKey(node any, key string) any {
	var out any
	walk(node, func(m map[string]any) {
		if out != nil {
			return
		}
		if v, ok := m[key]; ok && v != nil {
			out = v
		}
	})
	return out
}

// triviallyTrue: WHERE 1=1, WHERE 'a'='a', and OR-branches reducing to either.
func triviallyTrue(node any) bool {
	m, ok := node.(map[string]any)
	if !ok {
		return false
	}
	switch typeOf(m) {
	case "WhereClause":
		return triviallyTrue(m["SearchCondition"])
	case "BooleanComparisonExpression":
		if ct, _ := m["ComparisonType"].(string); ct == "Equals" {
			return sameLiteral(m["FirstExpression"], m["SecondExpression"])
		}
	case "BooleanBinaryExpression":
		op, _ := m["BinaryExpressionType"].(string)
		first := triviallyTrue(m["FirstExpression"])
		second := triviallyTrue(m["SecondExpression"])
		if strings.EqualFold(op, "Or") {
			return first || second
		}
		return first && second
	case "BooleanParenthesisExpression":
		return triviallyTrue(m["Expression"])
	}
	return false
}

func sameLiteral(a, b any) bool {
	am, ok1 := a.(map[string]any)
	bm, ok2 := b.(map[string]any)
	if !ok1 || !ok2 {
		return false
	}
	if !strings.HasSuffix(typeOf(am), "Literal") || typeOf(am) != typeOf(bm) {
		return false
	}
	av, _ := am["Value"].(string)
	bv, _ := bm["Value"].(string)
	return av != "" && av == bv
}

// schemaObjectName renders [database.]schema.base from a SchemaObjectName,
// defaulting the schema to dbo when the statement omits it.
func schemaObjectName(m map[string]any) string {
	get := func(key string) string {
		o, _ := m[key].(map[string]any)
		if o == nil {
			return ""
		}
		v, _ := o["Value"].(string)
		return v
	}
	base := get("BaseIdentifier")
	if base == "" {
		return ""
	}
	schema := get("SchemaIdentifier")
	if schema == "" {
		schema = "dbo" // ASSUMES the connection's default schema -- see findings
	}
	db := get("DatabaseIdentifier")
	if db != "" {
		return db + "." + schema + "." + base
	}
	return schema + "." + base
}

func firstTable(node any) string {
	name := ""
	walk(node, func(m map[string]any) {
		if name == "" && typeOf(m) == "SchemaObjectName" {
			name = schemaObjectName(m)
		}
	})
	return name
}

// writeTarget resolves the mutation's Target, not merely the first table
// mentioned -- an UPDATE ... FROM joins tables it does not write.
func writeTarget(stmt map[string]any) string {
	spec := findKey(stmt, "UpdateSpecification")
	for _, key := range []string{"DeleteSpecification", "InsertSpecification", "MergeSpecification"} {
		if spec == nil {
			spec = findKey(stmt, key)
		}
	}
	if spec == nil {
		return firstTable(stmt)
	}
	target := findKey(spec, "Target")
	if target == nil {
		return firstTable(stmt)
	}
	return firstTable(target)
}

func firstLine(s string) string {
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return s[:i]
	}
	return s
}

func main() {
	fmt.Println("=== SQL Server: sqlc-dev/teesql (pure Go, ScriptDom-shaped AST) ===")
	fmt.Println()

	pass, fail := 0, 0
	var failures []string
	for _, c := range corpus.TSQL {
		got := classify(c.SQL)
		ok := got.verdict == c.Want
		targetOK := true
		if ok && c.Target != "" && got.target != c.Target {
			targetOK = false
		}
		mark := "PASS"
		if !ok || !targetOK {
			mark = "FAIL"
			fail++
			failures = append(failures, fmt.Sprintf("%-20s want=%-8s got=%-8s target want=%q got=%q (%s)",
				c.Name, c.Want, got.verdict, c.Target, got.target, got.detail))
		} else {
			pass++
		}
		fmt.Printf("%s  %-20s %-8s %-24s %s\n", mark, c.Name, got.verdict, got.target, got.detail)
	}

	fmt.Println()
	fmt.Printf("RESULT: %d passed, %d failed, %d total\n", pass, fail, len(corpus.TSQL))
	if len(failures) > 0 {
		fmt.Println("\nFAILURES:")
		sort.Strings(failures)
		for _, f := range failures {
			fmt.Println("  " + f)
		}
	}

	fmt.Println("\n=== Parse latency ===")
	for _, sql := range []string{
		`SELECT * FROM orders WHERE id = 1`,
		`UPDATE [dbo].[orders] SET vip = 1 WHERE id IN (SELECT id FROM vips)`,
	} {
		_, _ = parseTree(sql)
		const n = 200
		start := time.Now()
		for i := 0; i < n; i++ {
			if _, err := parseTree(sql); err != nil {
				fmt.Println("  bench error:", err)
				break
			}
		}
		label := sql
		if len(label) > 40 {
			label = label[:40] + "..."
		}
		fmt.Printf("  %-45s %v/parse\n", label, time.Since(start)/n)
	}

	if fail > 0 {
		os.Exit(1)
	}
}
