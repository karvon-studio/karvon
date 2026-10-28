// Package report formats a store's stock for people.
package report

import (
	"fmt"
	"strings"

	"example.com/warehouse/stock"
)

// Money formats cents as 1,234.56.
func Money(cents int64) string {
	sign := ""
	if cents < 0 {
		sign, cents = "-", -cents
	}
	whole := fmt.Sprintf("%d", cents/100)
	var b strings.Builder
	for i, r := range whole {
		if i > 0 && (len(whole)-i)%3 == 0 {
			b.WriteByte(',')
		}
		b.WriteRune(r)
	}
	return fmt.Sprintf("%s%s.%02d", sign, b.String(), cents%100)
}

// Summary has a line per item, with what it is worth, and the total.
func Summary(s *stock.Store) string {
	var b strings.Builder
	for _, item := range s.Items() {
		fmt.Fprintf(&b, "%-6s %-10s %4d %10s\n", item.SKU, item.Name, item.Quantity, Money(item.UnitCents*int64(item.Quantity)))
	}
	fmt.Fprintf(&b, "%-6s %-10s %4s %10s\n", "", "Total", "", Money(s.Value()))
	return b.String()
}

// Reorder says how many of each low item to order to get it back to target.
func Reorder(s *stock.Store, threshold, target int) []string {
	var lines []string
	for _, item := range s.LowStock(threshold) {
		lines = append(lines, fmt.Sprintf("%s: order %d", item.SKU, target-item.Quantity))
	}
	return lines
}
