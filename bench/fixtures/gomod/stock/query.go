package stock

import (
	"slices"
	"strings"
)

// Items returns the store's items sorted by SKU.
func (s *Store) Items() []Item {
	items := make([]Item, 0, len(s.items))
	for _, item := range s.items {
		items = append(items, *item)
	}
	slices.SortFunc(items, func(a, b Item) int { return strings.Compare(a.SKU, b.SKU) })
	return items
}

// LowStock returns the items with fewer than threshold left, sorted by SKU.
func (s *Store) LowStock(threshold int) []Item {
	var low []Item
	for _, item := range s.Items() {
		if item.Quantity < threshold {
			low = append(low, item)
		}
	}
	return low
}
