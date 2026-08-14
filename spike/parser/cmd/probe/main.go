// Command probe dumps what teesql actually produces for a handful of
// statements, so the classifier is written against the real AST rather than
// against documentation.
package main

import (
	"context"
	"fmt"
	"strings"

	"github.com/sqlc-dev/teesql/parser"
)

func main() {
	samples := []string{
		`SELECT * FROM orders`,
		`UPDATE orders SET vip = 1 WHERE id = 42`,
		`UPDATE orders SET vip = 1`,
		`SELECT 1; DROP TABLE orders`,
		`EXEC('DELETE FROM orders')`,
		`UPDATE [dbo].[orders] SET vip = 1 WHERE id = 1`,
	}
	for _, s := range samples {
		fmt.Println("### ", s)
		script, err := parser.Parse(context.Background(), strings.NewReader(s))
		if err != nil {
			fmt.Println("  ERR:", err)
			continue
		}
		b, err := parser.MarshalScript(script)
		if err != nil {
			fmt.Println("  MARSHAL ERR:", err)
			continue
		}
		out := string(b)
		if len(out) > 1400 {
			out = out[:1400] + " ...[truncated]"
		}
		fmt.Println(out)
		fmt.Println()
	}
}
