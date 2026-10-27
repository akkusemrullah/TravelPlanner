using System.Collections.Specialized;

namespace TravelPlanner.Helpers
{
    /// <summary>Where a row sits in a grouped list card (TP-102): decides its rounded corners and top overlap.</summary>
    public enum ListPosition
    {
        Only,
        First,
        Middle,
        Last,
    }

    /// <summary>A list item that knows its <see cref="ListPosition"/> (raises PropertyChanged when it moves).</summary>
    public interface IListPositioned
    {
        ListPosition ListPosition { get; set; }
    }

    /// <summary>
    /// TP-102: the prototype shows lists as ONE card with dividers between the rows. A CollectionView can't wrap its
    /// rows in one Border, so every row draws its own piece of the card: rounded top on the first row, rounded bottom
    /// on the last, and each row after the first overlaps the one above by 1 so the two borders become the divider.
    /// <see cref="Track{T}"/> keeps the positions right as the collection changes.
    /// </summary>
    public static class ListPositions
    {
        public static void Track<T>(System.Collections.ObjectModel.ObservableCollection<T> items) where T : IListPositioned
        {
            items.CollectionChanged += (_, _) => Update(items);
            Update(items);
        }

        public static void Update<T>(IList<T> items) where T : IListPositioned
        {
            var count = items.Count;
            for (var i = 0; i < count; i++)
            {
                items[i].ListPosition = count == 1 ? ListPosition.Only
                    : i == 0 ? ListPosition.First
                    : i == count - 1 ? ListPosition.Last
                    : ListPosition.Middle;
            }
        }

        /// <summary>
        /// TP-104/105: a flat list drawn as several cards. Rows that aren't <see cref="IListPositioned"/> (section titles)
        /// end the card above them; <paramref name="startsCard"/> rows (a day header) start a new one.
        /// </summary>
        public static void AssignCards(IEnumerable<object> rows, Func<object, bool> startsCard)
        {
            var cards = new List<List<IListPositioned>>();
            List<IListPositioned>? card = null;
            foreach (var row in rows)
            {
                if (row is not IListPositioned slice)
                {
                    card = null;
                    continue;
                }
                if (card is null || startsCard(row))
                {
                    card = new List<IListPositioned>();
                    cards.Add(card);
                }
                card.Add(slice);
            }
            foreach (var each in cards)
                Update(each);
        }

        /// <summary>True when the row has a row above it (used for inset dividers in plain stacks).</summary>
        public static bool HasRowAbove(ListPosition position) => position is ListPosition.Middle or ListPosition.Last;
    }
}
