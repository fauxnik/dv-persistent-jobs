using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
	class JobChainControllerWithShuntingUnloadGeneration : JobChainController
	{
		public JobChainControllerWithShuntingUnloadGeneration(GameObject jobChainGO) : base(jobChainGO) { }

		protected override void OnLastJobInChainCompleted(Job lastJobInChain)
		{
			StaticJobDefinition lastJobDef = this.jobChain[this.jobChain.Count - 1];
			if (lastJobDef.job == lastJobInChain && lastJobInChain.jobType == JobType.ShuntingLoad)
			{
				StaticTransportJobDefinition loadJobDef = lastJobDef as StaticTransportJobDefinition;
				if (loadJobDef != null)
				{
					StationController startingStation = SingletonBehaviour<LogicController>.Instance
						.YardIdToStationController[loadJobDef.logicStation.ID];
					StationController destStation = SingletonBehaviour<LogicController>.Instance
						.YardIdToStationController[loadJobDef.chainData.chainDestinationYardId];
					Track startingTrack = loadJobDef.destinationTrack;
					List<TrainCar> trainCars = this.trainCarsForJobChain;
					System.Random rng = new System.Random(Environment.TickCount);
					JobChainController jobChainController
						= ShuntingUnloadJobProceduralGenerator.GenerateShuntingUnloadJobWithExistingCars(
							startingStation,
							startingTrack,
							destStation,
							trainCars,
							trainCars.Select<TrainCar, CargoType>(tc => tc.logicCar.CurrentCargoTypeInCar).ToList(),
							rng
						);
					if (jobChainController != null)
					{
						foreach (TrainCar tc in jobChainController.trainCarsForJobChain)
						{
							this.trainCarsForJobChain.Remove(tc);
						}
						jobChainController.FinalizeSetupAndGenerateFirstJob();
						Debug.Log(
							"Generated job chain [" + jobChainController.jobChainGO.name + "]: ",
							jobChainController.jobChainGO
						);
					}
				}
				else
				{
					Debug.LogError(
						"Couldn't convert lastJobDef to StaticTransportDefinition." +
						" ShuntingUnload jobs won't be generated."
					);
				}
			}
			else
			{
				Debug.LogError("Unexpected job chain format. Transport has to be last job in chain for JobChainControllerWithShuntingUnloadGeneration! ShuntingUnload jobs won't be generated.");
			}
			base.OnLastJobInChainCompleted(lastJobInChain);
		}
	}
}
