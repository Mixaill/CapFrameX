﻿using CapFrameX.View;
using Prism.Modularity;

namespace CapFrameX
{
    public class CapFrameXViewRegion : IModule
    {
        public void Initialize()
        {
            //RegionController.Singleton.RegisterViewWithRegion("MainRegion", typeof(Animation2DView));
            RegionController.Singleton.RegisterViewWithRegion("MainRegion", typeof(Animation3DView));
        }
    }
}