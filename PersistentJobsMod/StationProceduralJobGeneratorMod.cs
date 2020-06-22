using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PersistentJobsMod
{
    class StationProceduralJobGeneratorMod : StationProceduralJobGenerator
    {
        public StationProceduralJobGeneratorMod(StationController stationController) : base(stationController) { }

        public override JobChainController GenerateJobChain(System.Random rng, bool forceJobWithLicenseRequirementFulfilled)
        {
            Debug.LogError("StationProceduralJobGeneratorMod was installed properly!");
            return base.GenerateJobChain(rng, forceJobWithLicenseRequirementFulfilled);
        }
    }
}
