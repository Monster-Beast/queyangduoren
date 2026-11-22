using Queyang.Game;
using Xunit;

namespace Queyang.Core.Tests
{
    public class MotionPhaseTests
    {
        [Fact]
        public void EarlyPredictionHoldsInsteadOfReversingAndWrapsAtNativeLoopEnd()
        {
            var phase=new MotionPhase();
            phase.Anchor(15,19,90,true,1,0,false);
            Assert.Equal(0,phase.Frame(.045f));
            phase.Anchor(17,19,90,true,1.02f,.05f,true);
            Assert.Equal(0,phase.Frame(0));
            Assert.Equal(1,phase.Frame(.03f));
            phase.Anchor(1,19,90,true,1.06f,.04f,true);
            Assert.Equal(1,phase.Frame(0));
            Assert.Equal(3,phase.Frame(.02f));
            phase.Anchor(1,19,0,true,1.07f,.01f,false);
            Assert.Equal(1,phase.Frame(2));
        }
        [Fact]
        public void ReverseClipsOnceCompletionAndNewActionsFollowTheirOwnTimeline()
        {
            var phase=new MotionPhase();
            phase.Anchor(9,12,-20,false,1,0,false);
            Assert.Equal(7,phase.Frame(.1f));
            phase.Anchor(8,12,-20,false,1.05f,.1f,true);
            Assert.Equal(7,phase.Frame(0));
            phase.Anchor(1,12,-20,false,1.4f,.35f,true);
            Assert.Equal(0,phase.Frame(.1f));
            phase.Anchor(10,12,30,false,2,.6f,false);
            Assert.Equal(11,phase.Frame(.14f));
        }
    }
}
