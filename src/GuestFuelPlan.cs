using System;
using System.Collections.Generic;
namespace FarmProgress
{
    public static class GuestFuelPlan
    {
        // Ordinary farming actions use one tile; Move requires a source/destination pair.
        public static List<int[]> Ranges(int count, bool move, bool structural)
        {
            var result = new List<int[]>();
            int size = structural ? count : move ? 2 : 1;
            if (count <= 0 || move && count % 2 != 0) return result;
            for (int start = 0; start < count; start += size) result.Add(new[] { start, Math.Min(size, count - start) });
            return result;
        }
        public static uint Pay(uint fuel, uint cost) { return fuel >= cost ? fuel - cost : fuel; }
        public static uint Refill(uint fuel, uint amount, uint capacity) { return (uint)Math.Min(capacity, (ulong)fuel + amount); }
    }
}
