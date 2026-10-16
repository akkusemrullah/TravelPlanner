using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Xunit;
using TravelPlanner.Helpers;

namespace TravelPlanner.Tests
{
    public class RangeObservableCollectionTests
    {
        [Fact]
        public void ReplaceAll_SendsOneReset_AndNothingWhenUnchanged()
        {
            var a = new object();
            var b = new object();
            ObservableCollection<object> list = new RangeObservableCollection<object>(new[] { a });
            var events = new List<NotifyCollectionChangedAction>();
            list.CollectionChanged += (_, e) => events.Add(e.Action);

            list.ReplaceAll(new[] { b, a, new object() });
            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
            Assert.Equal(3, list.Count);
            Assert.Same(b, list[0]);

            list.ReplaceAll(list.ToList()); // the same rows again: the list control has nothing to redo
            Assert.Single(events);
        }

        [Fact]
        public void ReplaceAll_OnAPlainCollection_StillReplaces()
        {
            var list = new ObservableCollection<int> { 1, 2 };
            list.ReplaceAll(list.Where(i => i > 1).Append(3)); // a lazy query over the list itself
            Assert.Equal(new[] { 2, 3 }, list);
        }
    }
}
