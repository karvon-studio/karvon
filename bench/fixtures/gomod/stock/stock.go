// Package stock keeps a warehouse's items and how many of each are left.
package stock

import (
	"errors"
	"fmt"
)

// ErrUnknownSKU is returned for a SKU the store has never had.
var ErrUnknownSKU = errors.New("unknown SKU")

// Item is a product and how many of it are in the store.
type Item struct {
	SKU       string
	Name      string
	Quantity  int
	UnitCents int64
}

// Store holds items by SKU.
type Store struct {
	items map[string]*Item
}

// NewStore returns an empty store.
func NewStore() *Store {
	return &Store{items: map[string]*Item{}}
}

// Add puts an item in the store; for a SKU it has already, it adds to the quantity.
func (s *Store) Add(item Item) error {
	if item.SKU == "" {
		return errors.New("an item needs a SKU")
	}
	if item.Quantity < 0 {
		return fmt.Errorf("%s: negative quantity %d", item.SKU, item.Quantity)
	}
	if existing, ok := s.items[item.SKU]; ok {
		existing.Quantity += item.Quantity
		return nil
	}
	added := item
	s.items[item.SKU] = &added
	return nil
}

// Take removes quantity of an item, or fails when there are not that many left.
func (s *Store) Take(sku string, quantity int) error {
	item, ok := s.items[sku]
	if !ok {
		return fmt.Errorf("%w: %s", ErrUnknownSKU, sku)
	}
	if quantity > item.Quantity {
		return fmt.Errorf("%s: %d asked, %d left", sku, quantity, item.Quantity)
	}
	item.Quantity -= quantity
	return nil
}

// Get returns the item of a SKU.
func (s *Store) Get(sku string) (Item, bool) {
	item, ok := s.items[sku]
	if !ok {
		return Item{}, false
	}
	return *item, true
}

// Value is what everything in the store is worth, in cents.
func (s *Store) Value() int64 {
	var total int64
	for _, item := range s.items {
		total += int64(item.Quantity) * item.UnitCents
	}
	return total
}
