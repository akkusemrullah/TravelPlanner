using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;

namespace TravelPlanner.Tests
{
    public class ListPositionsTests
    {
        [Fact]
        public void Track_MarksFirstMiddleLast_AndFollowsChanges()
        {
            var items = new RangeObservableCollection<Trip>();
            ListPositions.Track(items);

            var a = new Trip(); var b = new Trip(); var c = new Trip();
            items.ReplaceAll(new[] { a, b, c });
            Assert.Equal(ListPosition.First, a.ListPosition);
            Assert.Equal(ListPosition.Middle, b.ListPosition);
            Assert.Equal(ListPosition.Last, c.ListPosition);

            items.Remove(c);
            Assert.Equal(ListPosition.Last, b.ListPosition);

            items.Remove(a);
            Assert.Equal(ListPosition.Only, b.ListPosition);
        }

        [Fact]
        public void ListPosition_RaisesPropertyChanged()
        {
            var trip = new Trip();
            var raised = new List<string?>();
            trip.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            trip.ListPosition = ListPosition.Last;

            Assert.Contains(nameof(Trip.ListPosition), raised);
        }

        [Theory]
        [InlineData(ListPosition.Only, 16, 16, 16, 16)]
        [InlineData(ListPosition.First, 16, 16, 0, 0)]
        [InlineData(ListPosition.Middle, 0, 0, 0, 0)]
        [InlineData(ListPosition.Last, 0, 0, 16, 16)]
        public void ShapeConverter_RoundsTheOuterCorners(ListPosition position, double tl, double tr, double bl, double br)
        {
            var shape = (RoundRectangle)new ListPositionShapeConverter().Convert(position, typeof(Shape), null, CultureInfo.InvariantCulture);
            Assert.Equal(new CornerRadius(tl, tr, bl, br), shape.CornerRadius);
        }

        [Theory]
        [InlineData(ListPosition.Only, 0)]
        [InlineData(ListPosition.First, 0)]
        [InlineData(ListPosition.Middle, -1)]
        [InlineData(ListPosition.Last, -1)]
        public void MarginConverter_OverlapsTheRowAbove(ListPosition position, double top)
        {
            var margin = (Thickness)new ListPositionMarginConverter().Convert(position, typeof(Thickness), "16", CultureInfo.InvariantCulture);
            Assert.Equal(new Thickness(16, top, 16, 0), margin);
        }
    }
}
