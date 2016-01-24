﻿using System;
using System.Collections.Generic;
using System.Threading;

using ICities;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using UnityEngine;

namespace EnhancedHearseAI
{
    public class Dispatcher : ThreadingExtensionBase
    {
        private Settings _settings;
        private Helper _helper;

        private string _collecting = ColossalFramework.Globalization.Locale.Get("VEHICLE_STATUS_HEARSE_COLLECT");
        private string _returning = ColossalFramework.Globalization.Locale.Get("VEHICLE_STATUS_HEARSE_RETURN");

        private bool _initialized;
        private bool _baselined;
        private bool _terminated;

        private Dictionary<ushort, Cemetery> _cemeteries;
        private Dictionary<ushort, Claimant> _master;
        private HashSet<ushort> _stopped;
        private HashSet<ushort> _updated;
        private uint _lastProcessedFrame;
        private Dictionary<ushort, HashSet<ushort>> _oldtargets;
        private Dictionary<ushort, ushort> _lasttargets;

        protected bool IsOverwatched()
        {
            #if DEBUG

            return true;

            #else

            foreach (var plugin in PluginManager.instance.GetPluginsInfo())
            {
                if (plugin.publishedFileID.AsUInt64 == 583538182)
                    return true;
            }

            return false;

            #endif
        }

        public override void OnCreated(IThreading threading)
        {
            _settings = Settings.Instance;
            _helper = Helper.Instance;

            _initialized = false;
            _baselined = false;
            _terminated = false;

            base.OnCreated(threading);
        }

        public override void OnBeforeSimulationTick()
        {
            if (_terminated) return;

            if (!_helper.GameLoaded)
            {
                _initialized = false;
                _baselined = false;
                return;
            }

            base.OnBeforeSimulationTick();
        }

        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            if (_terminated) return;

            if (!_helper.GameLoaded) return;

            try
            {
                if (!_initialized)
                {
                    if (!IsOverwatched())
                    {
                        _helper.NotifyPlayer("Skylines Overwatch not found. Terminating...");
                        _terminated = true;

                        return;
                    }

                    SkylinesOverwatch.Settings.Instance.Enable.BuildingMonitor = true;
                    SkylinesOverwatch.Settings.Instance.Enable.VehicleMonitor = true;

                    _cemeteries = new Dictionary<ushort, Cemetery>();
                    _master = new Dictionary<ushort, Claimant>();
                    _stopped = new HashSet<ushort>();
                    _updated = new HashSet<ushort>();
                    _oldtargets = new Dictionary<ushort, HashSet<ushort>>();
                    _lasttargets = new Dictionary<ushort, ushort>();

                    _initialized = true;

                    _helper.NotifyPlayer("Initialized");
                }
                else if (!_baselined)
                {
                    CreateBaseline();
                }
                else
                {
                    ProcessNewCemeteries();
                    ProcessRemovedCemeteries();

                    ProcessNewPickups();

                    if (!SimulationManager.instance.SimulationPaused)
                    {
                        UpdateHearses();
                    }

                    /*
                    if (SimulationManager.instance.SimulationPaused)
                    {
                        foreach (ushort id in _master.Keys)
                        {
                            string name = Singleton<BuildingManager>.instance.GetBuildingName(id, new InstanceID { Building = id });

                            if (!name.Contains("##"))
                                continue;

                            _helper.NotifyPlayer(String.Format("{0} ({1}) {2}/{3}", _master[id].Hearse, Math.Sqrt(_master[id].Distance), _master[id].IsValid, _master[id].IsChallengable));
                        }
                    }
                    */
                    _lastProcessedFrame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
                }
            }
            catch (Exception e)
            {
                string error = String.Format("Failed to {0}\r\n", !_initialized ? "initialize" : "update");
                error += String.Format("Error: {0}\r\n", e.Message);
                error += "\r\n";
                error += "==== STACK TRACE ====\r\n";
                error += e.StackTrace;

                _helper.Log(error);

                if (!_initialized)
                    _terminated = true;
            }

            base.OnUpdate(realTimeDelta, simulationTimeDelta);
        }

        public override void OnReleased()
        {
            _initialized = false;
            _baselined = false;
            _terminated = false;

            base.OnReleased();
        }

        private void CreateBaseline()
        {
            SkylinesOverwatch.Data data = SkylinesOverwatch.Data.Instance;

            foreach (ushort id in data.Cemeteries)
                _cemeteries.Add(id, new Cemetery(id, ref _master, ref _oldtargets));

            foreach (ushort pickup in data.BuildingsWithDead)
            {
                foreach (ushort id in _cemeteries.Keys)
                    _cemeteries[id].AddPickup(pickup);
            }

            _baselined = true;
        }

        private void ProcessNewCemeteries()
        {
            SkylinesOverwatch.Data data = SkylinesOverwatch.Data.Instance;

            foreach (ushort x in data.BuildingsUpdated)
            {
                if (!data.IsCemetery(x))
                    continue;

                if (_cemeteries.ContainsKey(x))
                    continue;
                
                _cemeteries.Add(x, new Cemetery(x, ref _master, ref _oldtargets));

                foreach (ushort pickup in data.BuildingsWithDead)
                    _cemeteries[x].AddPickup(pickup);
            }
        }

        private void ProcessRemovedCemeteries()
        {
            SkylinesOverwatch.Data data = SkylinesOverwatch.Data.Instance;

            foreach (ushort id in data.BuildingsRemoved)
                _cemeteries.Remove(id);
        }

        private void ProcessNewPickups()
        {
            SkylinesOverwatch.Data data = SkylinesOverwatch.Data.Instance;

            foreach (ushort pickup in data.BuildingsUpdated)
            {
                if (data.IsBuildingWithDead(pickup))
                {
                    foreach (ushort id in _cemeteries.Keys)
                        _cemeteries[id].AddPickup(pickup);
                }
                else
                {
                    foreach (ushort id in _cemeteries.Keys)
                        _cemeteries[id].AddCheckup(pickup);
                }
            }
        }

        private void UpdateHearses()
        {
            SkylinesOverwatch.Data data = SkylinesOverwatch.Data.Instance;
            Vehicle[] vehicles = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            InstanceID instanceID = new InstanceID();
            uint num1 = Singleton<SimulationManager>.instance.m_currentFrameIndex >> 4 & 7u;
            uint num2 = _lastProcessedFrame >> 4 & 7u;

            foreach (ushort vehicleID in data.VehiclesRemoved)
            {
                if (!data.IsHearse(vehicleID))
                    continue;

                if (_lasttargets.ContainsKey(vehicleID) && CheckDead(_lasttargets[vehicleID]))
                {
                    foreach (ushort id in _cemeteries.Keys)
                        _cemeteries[id].AddPickup(_lasttargets[vehicleID]);
                }
                _oldtargets.Remove(vehicleID);
                _lasttargets.Remove(vehicleID);
            }

            foreach (ushort vehicleID in data.VehiclesUpdated)
            {
                if (!data.IsHearse(vehicleID))
                    continue;

                Vehicle v = vehicles[vehicleID];

                /* 
                 * If a hearse is loading corpse, we will remove it from the vehicle grid,
                 * so other cars can pass it and more than one hearse can service a building.
                 * It doesn't really make sense that only one hearse can be at a high rise
                 * at a time.
                 */
                if ((v.m_flags & Vehicle.Flags.Stopped) != Vehicle.Flags.None)
                {                  
                    if (!_stopped.Contains(vehicleID))
                    {
                        Singleton<VehicleManager>.instance.RemoveFromGrid(vehicleID, ref vehicles[vehicleID], false);
                        _stopped.Add(vehicleID);
                    }

                    continue;
                }
                if (_stopped.Contains(vehicleID))
                {
                    _stopped.Remove(vehicleID);
                }
                else
                {
                    uint num3 = vehicleID & 7u;
                    if (num1 != num3 && num2 != num3)
                    {
                        _updated.Remove(vehicleID);
                        continue;
                    }
                    else if (_updated.Contains(vehicleID))
                    {
                        continue;
                    }
                }
                _updated.Add(vehicleID);

                if (!_cemeteries.ContainsKey(v.m_sourceBuilding))
                    continue;

                string localizedStatus = v.Info.m_vehicleAI.GetLocalizedStatus(vehicleID, ref v, out instanceID);

                if (localizedStatus == _returning && _lasttargets.ContainsKey(vehicleID))
                {
                    if (CheckDead(_lasttargets[vehicleID]))
                    {
                        foreach (ushort id in _cemeteries.Keys)
                            _cemeteries[id].AddPickup(_lasttargets[vehicleID]);
                    }
                    _lasttargets.Remove(vehicleID);
                    continue;
                }
                if (localizedStatus != _collecting) 
                    continue;

                if (_lasttargets.ContainsKey(vehicleID) && _lasttargets[vehicleID] != v.m_targetBuilding)
                {
                    _oldtargets.Remove(vehicleID);
                }
                ushort target = _cemeteries[v.m_sourceBuilding].AssignTarget(vehicleID);
                _lasttargets[vehicleID] = target;

                if (target != 0 && target != v.m_targetBuilding)
                {
                    if (CheckDead(v.m_targetBuilding)) 
                    {
                        foreach (ushort id in _cemeteries.Keys)
                            _cemeteries[id].AddPickup(v.m_targetBuilding);

                        if (!_oldtargets.ContainsKey(vehicleID))
                            _oldtargets.Add(vehicleID, new HashSet<ushort>());
                        _oldtargets[vehicleID].Add(v.m_targetBuilding);
                    }

                    v.Info.m_vehicleAI.SetTarget(vehicleID, ref vehicles[vehicleID], target);
                }
            }
        }

        private bool CheckDead(ushort buildingID)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            return buildings[buildingID].m_deathProblemTimer > 0;
        }
    }
}

