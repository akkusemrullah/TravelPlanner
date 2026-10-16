using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// An ObservableCollection whose content can be swapped in one go. <c>Clear()</c> + one <c>Add()</c> per row
    /// sends the list control one event per row; on Android every one of them makes CollectionView update and
    /// re-layout, which is what made a page stutter while it slid in. <see cref="ReplaceAll"/> sends a single
    /// Reset instead, and nothing at all when the rows didn't change.
    /// </summary>
    public class RangeObservableCollection<T> : ObservableCollection<T>
    {
        public RangeObservableCollection()
        {
        }

        public RangeObservableCollection(IEnumerable<T> items) : base(items)
        {
        }

        /// <summary>Replaces every item, raising one Reset (none when the new items are the same objects in the same order).</summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            CheckReentrancy();
            var list = items as IList<T> ?? items.ToList();
            if (SameItems(list))
                return;

            Items.Clear();
            foreach (var item in list)
                Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private bool SameItems(IList<T> list)
        {
            if (list.Count != Items.Count)
                return false;
            for (var i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i], Items[i]))
                    return false;
            }
            return true;
        }
    }

    public static class ObservableCollectionExtensions
    {
        /// <summary>
        /// Swaps a list's content: in one event for a <see cref="RangeObservableCollection{T}"/>, the slow way for
        /// any other collection.
        /// </summary>
        public static void ReplaceAll<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
        {
            if (collection is RangeObservableCollection<T> range)
            {
                range.ReplaceAll(items);
                return;
            }

            var list = items.ToList();
            collection.Clear();
            foreach (var item in list)
                collection.Add(item);
        }
    }
}
