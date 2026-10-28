package report

import (
	"slices"
	"testing"

	"example.com/warehouse/stock"
)

func store(t *testing.T) *stock.Store {
	t.Helper()
	s := stock.NewStore()
	for _, item := range []stock.Item{
		{SKU: "A-100", Name: "Anchor", Quantity: 3, UnitCents: 250},
		{SKU: "B-200", Name: "Bolt M6", Quantity: 40, UnitCents: 12},
	} {
		if err := s.Add(item); err != nil {
			t.Fatal(err)
		}
	}
	return s
}

func TestMoney(t *testing.T) {
	for cents, want := range map[int64]string{0: "0.00", 5: "0.05", 1250: "12.50", 123456789: "1,234,567.89", -99950: "-999.50"} {
		if got := Money(cents); got != want {
			t.Errorf("Money(%d) = %q, want %q", cents, got, want)
		}
	}
}

func TestSummary(t *testing.T) {
	want := "" +
		"A-100  Anchor        3       7.50\n" +
		"B-200  Bolt M6      40       4.80\n" +
		"       Total                12.30\n"
	if got := Summary(store(t)); got != want {
		t.Errorf("Summary() =\n%s\nwant\n%s", got, want)
	}
}

func TestReorder(t *testing.T) {
	got := Reorder(store(t), 10, 25)
	if want := []string{"A-100: order 22"}; !slices.Equal(got, want) {
		t.Errorf("Reorder() = %v, want %v", got, want)
	}
}
