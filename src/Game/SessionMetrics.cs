using System;
using System.Diagnostics;
using System.Globalization;

namespace Queyang.Game
{
    // Periodic aggregates contain only timings and counts. No colony or account data is logged.
    internal sealed class SessionMetrics
    {
        readonly double[] frameMs = new double[2048], commandMs = new double[256];
        long frames, commands, sent, received;
        double since;
        readonly string role;

        internal SessionMetrics(string role, double time) { this.role=role; since=time; }
        internal void Sent(int size) => sent+=size;
        internal void Received(int size) => received+=size;
        internal void RecordCommand(double ms) => commandMs[commands++%commandMs.Length]=ms;
        static double Percentile(double[] samples,long total)
        {
            var count=(int)Math.Min(total,samples.Length);
            if (count==0) return 0;
            var sorted=new double[count];
            Array.Copy(samples,sorted,count); Array.Sort(sorted);
            return sorted[(int)Math.Ceiling(count*0.95)-1];
        }
        internal void Tick(long start,double time)
        {
            frameMs[frames++%frameMs.Length]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
            if(time-since<60) return;
            var span=time-since;
            UnityEngine.Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[QueyangCoop] role={0} seconds={1:F1} pumpP95ms={2:F3} commandP95ms={3:F1} commands={4} txBps={5:F0} rxBps={6:F0}",
                role,span,Percentile(frameMs,frames),Percentile(commandMs,commands),commands,sent/span,received/span));
            frames=commands=sent=received=0; since=time;
        }
    }
}
