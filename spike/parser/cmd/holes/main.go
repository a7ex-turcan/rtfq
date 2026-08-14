// Command holes examines the two gaps the corpus run exposed, both of which
// survive a correct statement-type allow-list:
//
//  1. T-SQL: INSERT ... EXEC runs a stored procedure but surfaces only as
//     InsertStatement.
//  2. PostgreSQL: dblink_exec / lo_export / pg_sleep write, exfiltrate, or stall
//     from inside a statement that classifies as a plain SELECT.
//
// Both are function/source-level, not statement-level. This checks whether the
// parse tree exposes enough to close them.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"strings"

	pgquery "github.com/wasilibs/go-pgquery"
	"github.com/sqlc-dev/teesql/parser"
)

func main() {
	fmt.Println("=== HOLE 1: T-SQL INSERT ... EXEC ===")
	for _, sql := range []string{
		`INSERT INTO orders EXEC sp_getorders`,
		`INSERT INTO orders EXEC ('DELETE FROM x')`,
		`INSERT INTO orders (id) VALUES (1)`,
		`INSERT INTO orders SELECT * FROM staging`,
	} {
		fmt.Printf("  %-42s InsertSource=%s\n", sql, insertSourceType(sql))
	}

	fmt.Println("\n=== HOLE 2: PostgreSQL function calls inside a read ===")
	for _, sql := range []string{
		`SELECT dblink_exec('host=evil', 'DELETE FROM orders')`,
		`SELECT lo_export(1, '/tmp/x')`,
		`SELECT pg_read_file('/etc/passwd')`,
		`SELECT pg_sleep(10)`,
		`SELECT count(*) FROM orders WHERE created_at > now()`,
		`UPDATE orders SET vip = true WHERE id = 1 AND pg_sleep(10) IS NULL`,
	} {
		fmt.Printf("  %-56s funcs=%v\n", trunc(sql, 54), pgFunctions(sql))
	}
}

// insertSourceType reveals what feeds an INSERT. ExecuteInsertSource means a
// stored procedure runs; ValuesInsertSource and SelectInsertSource do not.
func insertSourceType(sql string) string {
	script, err := parser.Parse(context.Background(), strings.NewReader(sql))
	if err != nil {
		return "PARSE ERROR"
	}
	b, err := parser.MarshalScript(script)
	if err != nil {
		return "MARSHAL ERROR"
	}
	var tree any
	_ = json.Unmarshal(b, &tree)

	found := ""
	var rec func(any)
	rec = func(n any) {
		switch t := n.(type) {
		case map[string]any:
			if src, ok := t["InsertSource"].(map[string]any); ok && found == "" {
				found, _ = src["$type"].(string)
			}
			for _, v := range t {
				rec(v)
			}
		case []any:
			for _, v := range t {
				rec(v)
			}
		}
	}
	rec(tree)
	if found == "" {
		return "(not exposed)"
	}
	return found
}

// pgFunctions extracts every function name called anywhere in the statement.
// If this works, a function deny-list is implementable as a guard layer.
func pgFunctions(sql string) []string {
	js, err := pgquery.ParseToJSON(sql)
	if err != nil {
		return []string{"PARSE ERROR"}
	}
	var tree any
	_ = json.Unmarshal([]byte(js), &tree)

	seen := map[string]bool{}
	var rec func(any)
	rec = func(n any) {
		switch t := n.(type) {
		case map[string]any:
			if fc, ok := t["FuncCall"].(map[string]any); ok {
				if name := dottedName(fc["funcname"]); name != "" {
					seen[name] = true
				}
			}
			for _, v := range t {
				rec(v)
			}
		case []any:
			for _, v := range t {
				rec(v)
			}
		}
	}
	rec(tree)

	out := make([]string, 0, len(seen))
	for k := range seen {
		out = append(out, k)
	}
	sort.Strings(out)
	if len(out) == 0 {
		return []string{"(none)"}
	}
	return out
}

func dottedName(v any) string {
	list, _ := v.([]any)
	var parts []string
	for _, item := range list {
		m, _ := item.(map[string]any)
		if m == nil {
			continue
		}
		s, _ := m["String"].(map[string]any)
		if s == nil {
			continue
		}
		if sv, ok := s["sval"].(string); ok {
			parts = append(parts, sv)
		}
	}
	return strings.Join(parts, ".")
}

func trunc(s string, n int) string {
	if len(s) > n {
		return s[:n] + "..."
	}
	return s
}
