using Harmony12;
using System.Collections.Generic;
using UnityEngine;

namespace PersistentJobsMod
{
	static class PJModSettings_PurgeData_Patch
	{
		static void Postfix()
		{
			Debug.Log("Clearing passenger spawning block list...");
			Main.stationIdPassengerBlockList.Clear();
		}
	}

	static class PassengerJobGenerator_StartGenerationAsync_Patch
	{
		static bool Prefix(object __instance)
		{
			if (Main.modEntry.Active)
			{
				StationController controller = Traverse.Create(__instance).Field("Controller").GetValue<StationController>();
				string stationId = controller?.logicStation?.ID;
				return stationId == null || !Main.stationIdPassengerBlockList.Contains(controller.logicStation.ID);
			}
			return true;
		}

		static void Postfix(object __instance)
		{
			StationController controller = Traverse.Create(__instance).Field("Controller").GetValue<StationController>();
			string stationId = controller?.logicStation?.ID;
			if (stationId != null && !Main.stationIdPassengerBlockList.Contains(stationId))
			{
				Main.stationIdPassengerBlockList.Add(stationId);
			}
		}
	}

	static class CarSpawner_DeleteTrainCars_Replacer
    {
		static void NoOp(List<TrainCar> trainCarsToDelete, bool forceInstantDestroy = false) { }
		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
			instructions = instructions.MethodReplacer(
				AccessTools.Method(typeof(CarSpawner), "DeleteTrainCars"),
				AccessTools.Method(typeof(CarSpawner_DeleteTrainCars_Replacer), "NoOp"));

			foreach(var instruction in instructions)
            {
				var operandString = instruction.operand?.ToString();
				//Debug.Log($"[IL debug] {operandString}");
				var isCarSpawnerGetInstance = operandString?.Contains("CarSpawner get_Instance()");
				if (!isCarSpawnerGetInstance.HasValue || isCarSpawnerGetInstance.Value == false)
                {
					yield return instruction;
                }
            }
        }
    }
}
