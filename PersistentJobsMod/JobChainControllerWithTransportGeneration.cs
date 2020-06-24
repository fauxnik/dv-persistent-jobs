using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
	class JobChainControllerWithTransportGeneration : JobChainController
	{
		public JobChainControllerWithTransportGeneration(GameObject jobChainGO) : base(jobChainGO) { }

		protected override void OnLastJobInChainCompleted(Job lastJobInChain)
		{
			StaticJobDefinition lastJobDef = this.jobChain[this.jobChain.Count - 1];
			if (lastJobDef.job == lastJobInChain && lastJobInChain.jobType == JobType.ShuntingLoad)
			{
				StaticShuntingLoadJobDefinition loadJobDef = lastJobDef as StaticShuntingLoadJobDefinition;
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
						= TransportJobProceduralGenerator.GenerateTransportJobWithExistingCars(
							startingStation,
							startingTrack,
							destStation,
							trainCars,
							trainCars.Select<TrainCar, CargoType>(tc => tc.logicCar.CurrentCargoTypeInCar).ToList(),
							trainCars.Select<TrainCar, float>(tc => tc.logicCar.LoadedCargoAmount).ToList(),
							rng
						);
					if (jobChainController != null)
					{
						for (int j = 0; j < trainCars.Count; j++)
						{
							this.trainCarsForJobChain.Remove(trainCars[j]);
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
						"Couldn't convert lastJobDef to StaticShuntingLoadDefinition." +
						" Transport jobs won't be generated."
					);
				}
			}
			else
			{
				Debug.LogError(
					"Unexpected job chain format. ShuntingLoad has to be last job in chain for" +
					" JobChainControllerWithTransportGeneration! Transport jobs won't be generated."
				);
			}
			base.OnLastJobInChainCompleted(lastJobInChain);
		}
	}
}
