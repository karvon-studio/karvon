package stock

import (
	"errors"
	"testing"
)

func sample(t *testing.T) *Store {
	t.Helper()
	s := NewStore()
	for _, item := range []Item{
		{SKU: "B-200", Name: "Bolt M6", Quantity: 40, UnitCents: 12},
		{SKU: "A-100", Name: "Anchor", Quantity: 3, UnitCents: 250},
		{SKU: "C-300", Name: "Clamp", Quantity: 8, UnitCents: 499},
	} {
		if err := s.Add(item); err != nil {
			t.Fatal(err)
		}
	}
	return s
}

func TestAddMergesTheSameSKU(t *testing.T) {
	s := sample(t)
	if err := s.Add(Item{SKU: "A-100", Name: "Anchor", Quantity: 2, UnitCents: 250}); err != nil {
		t.Fatal(err)
	}
	if item, _ := s.Get("A-100"); item.Quantity != 5 {
		t.Fatalf("quantity = %d, want 5", item.Quantity)
	}
}

func TestAddRejectsBadItems(t *testing.T) {
	s := NewStore()
	if err := s.Add(Item{Name: "no SKU"}); err == nil {
		t.Error("an item without a SKU was added")
	}
	if err := s.Add(Item{SKU: "X", Quantity: -1}); err == nil {
		t.Error("a negative quantity was added")
	}
}

func TestTake(t *testing.T) {
	s := sample(t)
	if err := s.Take("C-300", 5); err != nil {
		t.Fatal(err)
	}
	if item, _ := s.Get("C-300"); item.Quantity != 3 {
		t.Errorf("quantity = %d, want 3", item.Quantity)
	}
	if err := s.Take("C-300", 4); err == nil {
		t.Error("took more than was left")
	}
	if err := s.Take("Z-999", 1); !errors.Is(err, ErrUnknownSKU) {
		t.Errorf("err = %v, want ErrUnknownSKU", err)
	}
}

func TestValue(t *testing.T) {
	if got := sample(t).Value(); got != 40*12+3*250+8*499 {
		t.Errorf("Value() = %d", got)
	}
}

func TestItemsAreSortedBySKU(t *testing.T) {
	items := sample(t).Items()
	if len(items) != 3 || items[0].SKU != "A-100" || items[2].SKU != "C-300" {
		t.Errorf("Items() = %v", items)
	}
}

func TestLowStock(t *testing.T) {
	low := sample(t).LowStock(10)
	if len(low) != 2 || low[0].SKU != "A-100" || low[1].SKU != "C-300" {
		t.Errorf("LowStock(10) = %v", low)
	}
}
