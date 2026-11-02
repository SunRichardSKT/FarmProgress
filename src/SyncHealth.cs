using System;
namespace FarmProgress
{
    public sealed class SyncHealth
    {
        public int Checks { get; private set; }
        public int Mismatches { get; private set; }
        public double LastTick { get; private set; }
        public bool HasTick { get; private set; }
        public SyncHealth(double now) { LastTick = now; }
        public void Tick(double now) { LastTick = now; HasTick = true; }
        public bool Check(long local, long host)
        { Checks++; if (local == host) return false; Mismatches++; return true; }
        public bool Stalled(double now) { return now - LastTick >= 15; }
        public string Describe(double now)
        {
            if (Stalled(now)) return "超过 15 秒未收到农场时钟；连接可能停滞。";
            if (Mismatches > 0) return "发现 " + Mismatches + " 次状态分歧；建议重新加入，重新下载房主数据。";
            if (Checks > 0) return "已收到 " + Checks + " 次随机状态校验，尚未发现差异（非全量地图校验）。";
            return "等待房主校验；暂未确认双方数据一致。";
        }
    }
}
