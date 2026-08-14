// Command failopen isolates the dangerous version of teesql's no-error
// behaviour: when it meets syntax it cannot parse, does it DROP that syntax and
// hand back a benign subset?
//
// If it does, the guard would authorise the subset while SQL Server executes
// the whole string. That is a gate bypass, and it decides whether teesql is
// usable without a wrapper.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/sqlc-dev/teesql/parser"
)

func main() {
	cases := []struct{ name, sql string }{
		{"garbage-only", `@@@ not sql at all @@@`},
		{"truncated", `SELECT * FROM`},
		{"unterminated-literal", `SELECT * FROM orders WHERE n = 'abc`},
		{"benign-then-garbage", `SELECT 1 @@@@ DELETE FROM orders`},
		{"write-then-garbage", `UPDATE orders SET vip = 1 WHERE id = 1 @@@ DROP TABLE x`},
		{"garbage-then-drop", `@@@ DROP TABLE orders`},
		{"comment-only", `-- just a comment`},
		{"empty", ``},
	}

	for _, c := range cases {
		fmt.Printf("### %-22s %q\n", c.name, c.sql)
		script, err := parser.Parse(context.Background(), strings.NewReader(c.sql))
		if err != nil {
			fmt.Println("    error:", err)
			fmt.Println()
			continue
		}
		b, err := parser.MarshalScript(script)
		if err != nil {
			fmt.Println("    marshal error:", err)
			fmt.Println()
			continue
		}
		var tree map[string]any
		_ = json.Unmarshal(b, &tree)

		stmts := statements(tree)
		fmt.Printf("    err=nil  batches=%d  statements=%d  types=%v\n",
			len(batches(tree)), len(stmts), types(stmts))

		// Coverage: how much of the input did the parse actually account for?
		// ScriptDom-shaped ASTs carry StartOffset/FragmentLength, so we can ask
		// whether the tree spans the whole string.
		covered := coverage(tree)
		fmt.Printf("    input=%d bytes, covered by statements=%d bytes -> %s\n",
			len(c.sql), covered, verdict(len(c.sql), covered, len(stmts)))

		// Does the marshalled tree mention anything error-shaped?
		if s := string(b); strings.Contains(strings.ToLower(s), "error") {
			fmt.Println("    NOTE: tree contains an 'error'-shaped node")
		}
		fmt.Println()
	}
}

func batches(tree map[string]any) []any {
	b, _ := tree["Batches"].([]any)
	return b
}

func statements(tree map[string]any) []map[string]any {
	var out []map[string]any
	for _, b := range batches(tree) {
		bm, _ := b.(map[string]any)
		if bm == nil {
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

func types(stmts []map[string]any) []string {
	var out []string
	for _, s := range stmts {
		t, _ := s["$type"].(string)
		out = append(out, t)
	}
	if out == nil {
		return []string{"(none)"}
	}
	return out
}

// coverage returns the highest end-offset reached by any statement fragment.
func coverage(tree map[string]any) int {
	max := 0
	for _, s := range statements(tree) {
		start := intOf(s["StartOffset"])
		length := intOf(s["FragmentLength"])
		if start+length > max {
			max = start + length
		}
	}
	return max
}

func intOf(v any) int {
	if f, ok := v.(float64); ok {
		return int(f)
	}
	return 0
}

func verdict(input, covered, nstmts int) string {
	switch {
	case nstmts == 0:
		return "NO STATEMENTS (guard rejects: safe)"
	case covered < input:
		return "*** UNCOVERED TAIL: parser dropped input the server would still run ***"
	default:
		return "fully covered"
	}
}
