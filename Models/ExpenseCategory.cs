using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TravelPlanner.Models
{
    /// <summary>
    /// sqlite-net-pcl stores enum properties as their underlying integer value
    /// automatically, so no custom converter is needed.
    /// </summary>
    public enum ExpenseCategory
    {
        Transportation = 0,
        Accommodation = 1,
        Food = 2,
        Entertainment = 3,
        Shopping = 4,
        Other = 5
    }
}
