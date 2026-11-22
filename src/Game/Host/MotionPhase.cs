using System;

namespace Queyang.Game
{
    // Keep an unwrapped time line so correcting a late packet never plays the preceding
    // frames again. If prediction is ahead, hold until the host catches up.
    internal struct MotionPhase
    {
        double authority, shown;
        float rate, stamp;
        int lastFrame, frames;
        bool loop;
        internal void Anchor(int frame,int count,float speed,bool repeats,float hostTime,float arrivalInterval,bool continuing)
        {
            int direction=Math.Sign(speed);
            bool same=continuing && direction!=0 && Math.Sign(rate)==direction && frames==count &&
                repeats==loop && count>0 && frame>=0 && lastFrame>=0;
            int step=(frame-lastFrame)*direction;
            if(same && (repeats || step>=0))
            {
                if(repeats)
                {
                    step=(step%count+count)%count;
                    float dt=hostTime>=0 && stamp>=0?hostTime-stamp:arrivalInterval;
                    if(dt>0 && dt<.5f)
                        step+=count*Math.Max(0,(int)Math.Round((Math.Abs(speed)*dt-step)/count));
                }
                authority+=step*direction;
            }
            else authority=shown=frame;
            lastFrame=frame; frames=count; rate=speed; loop=repeats; stamp=hostTime;
        }

        internal int Frame(float age)
        {
            if(lastFrame<0 || frames<1 || rate==0) return lastFrame;
            double target=authority+rate*Math.Min(.14f,Math.Max(0,age));
            shown=rate>0?Math.Max(shown,target):Math.Min(shown,target);
            if(!loop) shown=Math.Max(0,Math.Min(frames-1,shown));
            int n=(int)Math.Floor(shown+.5);
            return loop?(n%frames+frames)%frames:n;
        }
    }
}
