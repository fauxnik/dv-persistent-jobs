using Harmony12;
using UnityEngine;

namespace PersistentJobsMod
{
	class PJModSettings_PurgeData_Patch
	{
		static void Postfix()
		{
			Debug.Log("Clearing passenger spawning block list...");
			Main.stationIdPassengerBlockList.Clear();
		}
	}

	class PassengerJobGenerator_StartGenerationAsync_Patch
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
}
