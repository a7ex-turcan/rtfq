// Command robust answers the question a corpus pass cannot: when the parser
// meets syntax it does not know, does it ERROR (fail closed) or hand back a
// partial tree the guard would then wave through (fail open)?
//
// A fail-open parser is disqualified no matter how well it scores on a corpus.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	pgquery "github.com/wasilibs/go-pgquery"
	"github.com/sqlc-dev/teesql/parser"
)

type probe struct {
	name string
	sql  string
	// wantErr: we require a parse error, because silently accepting this
	// would mean the guard reasons about an incomplete tree.
	wantErr bool
	why     string
}

var pgProbes = []probe{
	{"garbage", `@@@ not sql at all @@@`, true, "pure nonsense must not parse"},
	{"truncated", `SELECT * FROM`, true, "incomplete statement"},
	{"unbalanced-quote", `SELECT * FROM orders WHERE n = 'abc`, true, "unterminated literal"},
	{"comment-only", `-- just a comment`, true, "no statement to authorise"},
	{"empty", ``, true, "nothing to run"},
	{"pg18-syntax", `SELECT * FROM orders WHERE id = 1 FOR UPDATE SKIP LOCKED`, false, "valid modern PG must parse"},
	{"json-table", `SELECT * FROM JSON_TABLE('[]'::jsonb, '$[*]' COLUMNS (a int PATH '$.a'))`, false, "PG17 JSON_TABLE"},
	{"nested-parens", "SELECT " + strings.Repeat("(", 2000) + "1" + strings.Repeat(")", 2000), false, "deep nesting: parse or bounded error, never hang/crash"},
}

var tsqlProbes = []probe{
	{"garbage", `@@@ not sql at all @@@`, true, "pure nonsense must not parse"},
	{"truncated", `SELECT * FROM`, true, "incomplete statement"},
	{"unbalanced-quote", `SELECT * FROM orders WHERE n = 'abc`, true, "unterminated literal"},
	{"comment-only", `-- just a comment`, true, "no statement to authorise"},
	{"empty", ``, true, "nothing to run"},
	{"nested-parens", "SELECT " + strings.Repeat("(", 2000) + "1" + strings.Repeat(")", 2000), false, "deep nesting"},
}

// Statements that must not be classified as a plain read/write even though
// they are valid: each one is a nested or side-effecting construct.
var tsqlNested = []string{
	`IF 1=1 DELETE FROM orders`,
	`BEGIN DELETE FROM orders END`,
	`WHILE 1=1 UPDATE orders SET vip = 1 WHERE id = 1`,
	`BEGIN TRAN DELETE FROM orders WHERE id = 1`,
	`WAITFOR DELAY '00:00:10'`,
	`SELECT * FROM OPENROWSET('SQLNCLI','...','SELECT 1')`,
	`INSERT INTO orders EXEC sp_getorders`,
	`UPDATE o SET vip = 1 FROM orders o JOIN vips v ON o.id = v.id WHERE v.tier = 1`,
}

var pgNested = []string{
	`CREATE TEMP TABLE x AS SELECT * FROM orders`,
	`SELECT * FROM orders WHERE id = (SELECT pg_sleep(10))::int`,
	`SELECT dblink_exec('host=evil', 'DELETE FROM orders')`,
	`INSERT INTO orders SELECT * FROM staging`,
	`UPDATE orders o SET vip = v.vip FROM vips v WHERE o.id = v.id`,
	`SELECT lo_export(1, '/tmp/x')`,
	`REFRESH MATERIALIZED VIEW mv`,
	`CALL do_something()`,
}

func main() {
	fmt.Println("=== FAIL-CLOSED PROBE ===")
	fmt.Println()

	fmt.Println("-- PostgreSQL (go-pgquery) --")
	for _, p := range pgProbes {
		start := time.Now()
		_, err := pgquery.ParseToJSON(p.sql)
		el := time.Since(start)
		report(p, err != nil, el, err)
	}

	fmt.Println("\n-- SQL Server (teesql) --")
	for _, p := range tsqlProbes {
		start := time.Now()
		_, err := parser.Parse(context.Background(), strings.NewReader(p.sql))
		el := time.Since(start)
		report(p, err != nil, el, err)
	}

	fmt.Println("\n=== NODE TYPES for constructs the allow-list must judge ===")
	fmt.Println("(shows what the guard would see; anything outside the allow-list is refused)")

	fmt.Println("\n-- PostgreSQL --")
	for _, sql := range pgNested {
		fmt.Printf("  %-62s -> %s\n", trunc(sql, 60), pgTopTypes(sql))
	}

	fmt.Println("\n-- SQL Server --")
	for _, sql := range tsqlNested {
		fmt.Printf("  %-62s -> %s\n", trunc(sql, 60), tsqlTopTypes(sql))
	}
}

func report(p probe, gotErr bool, el time.Duration, err error) {
	status := "OK   "
	if gotErr != p.wantErr {
		status = "NOTE "
	}
	desc := "parsed"
	if gotErr {
		desc = "error: " + trunc(firstLine(fmt.Sprint(err)), 55)
	}
	fmt.Printf("  %s %-18s wantErr=%-5v %-64s %v\n", status, p.name, p.wantErr, desc, el.Round(time.Microsecond))
}

func pgTopTypes(sql string) string {
	js, err := pgquery.ParseToJSON(sql)
	if err != nil {
		return "PARSE ERROR (rejected)"
	}
	var tree any
	_ = json.Unmarshal([]byte(js), &tree)
	seen := map[string]bool{}
	var rec func(any)
	rec = func(n any) {
		switch t := n.(type) {
		case map[string]any:
			for k, v := range t {
				if strings.HasSuffix(k, "Stmt") && k[0] >= 'A' && k[0] <= 'Z' {
					seen[k] = true
				}
				rec(v)
			}
		case []any:
			for _, v := range t {
				rec(v)
			}
		}
	}
	rec(tree)
	return join(seen)
}

func tsqlTopTypes(sql string) string {
	script, err := parser.Parse(context.Background(), strings.NewReader(sql))
	if err != nil {
		return "PARSE ERROR (rejected)"
	}
	b, err := parser.MarshalScript(script)
	if err != nil {
		return "MARSHAL ERROR"
	}
	var tree any
	_ = json.Unmarshal(b, &tree)
	seen := map[string]bool{}
	var rec func(any)
	rec = func(n any) {
		switch t := n.(type) {
		case map[string]any:
			if ty, _ := t["$type"].(string); strings.HasSuffix(ty, "Statement") {
				seen[ty] = true
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
	return join(seen)
}

func join(m map[string]bool) string {
	var out []string
	for k := range m {
		out = append(out, k)
	}
	if len(out) == 0 {
		return "(none)"
	}
	return strings.Join(out, ", ")
}

func trunc(s string, n int) string {
	s = strings.ReplaceAll(s, "\n", " ")
	if len(s) > n {
		return s[:n] + "..."
	}
	return s
}

func firstLine(s string) string {
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return s[:i]
	}
	return s
}
