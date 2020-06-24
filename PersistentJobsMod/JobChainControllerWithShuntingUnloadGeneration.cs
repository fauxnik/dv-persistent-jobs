using System;
using System.Collections.Generic;
using UnityEngine;
using DV.Logic.Job;

namespace PersistentJobsMod
{
	class JobChainControllerWithShuntingUnloadGeneration : JobChainController
	{
		public JobChainControllerWithShuntingUnloadGeneration(GameObject jobChainGO) : base(jobChainGO) { }

		protected override void OnLastJobInChainCompleted(Job lastJobInChain)
		{
			StaticJobDefinition staticJobDefinition = this.jobChain[this.jobChain.Count - 1];
			if (staticJobDefinition.job == lastJobInChain && lastJobInChain.jobType == JobType.Transport)
			{
				StaticTransportJobDefinition staticTransportJobDefinition = staticJobDefinition as StaticTransportJobDefinition;
				if (staticTransportJobDefinition != null)
				{
					StationController startingStation = SingletonBehaviour<LogicController>.Instance.YardIdToStationController[staticTransportJobDefinition.logicStation.ID];
					StationController destStation = SingletonBehaviour<LogicController>.Instance.YardIdToStationController[staticTransportJobDefinition.job.chainData.chainDestinationYardId];
					staticTransportJobDefinition.job.
					System.Random rng = new System.Random(Environment.TickCount);
					List<CarsPerTrack> carsPerDestinationTrack = staticTransportJobDefinition.carsPerDestinationTrack;
					for (int i = 0; i < carsPerDestinationTrack.Count; i++)
					{
						Track track = carsPerDestinationTrack[i].track;
						List<TrainCar> list = Utilities.ExtractCorrespondingTrainCars(this, carsPerDestinationTrack[i].cars);
						if (list == null)
						{
							Debug.LogError("Couldn't find all corresponding trainCars from trainCarsForJobChain!");
							break;
						}
						JobChainController jobChainController = ShuntingUnloadJobProceduralGenerator.GenerateShuntingUnloadJobWithExistingCars(startingStation, track, list, rng);
						if (jobChainController != null)
						{
							for (int j = 0; j < list.Count; j++)
							{
								this.trainCarsForJobChain.Remove(list[j]);
							}
							jobChainController.FinalizeSetupAndGenerateFirstJob();
							Debug.Log("Generated job chain [" + jobChainController.jobChainGO.name + "]: ", jobChainController.jobChainGO);
						}
					}
				}
				else
				{
					Debug.LogError("Couldn't convert lastJobDef to StaticTransportDefinition. ShuntingUnload jobs won't be generated.");
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
