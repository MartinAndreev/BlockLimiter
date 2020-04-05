﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using BlockLimiter.Patch;
using BlockLimiter.ProcessHandlers;
using BlockLimiter.Punishment;
using BlockLimiter.Settings;
using BlockLimiter.Utility;
using NLog;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.API.Plugins;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.Managers.PatchManager;
using Torch.API.Session;
using Torch.Session;
using Torch.Views;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.VisualScripting;
using VRage.Network;
using VRage.Profiler;
using Grid = BlockLimiter.Utility.Grid;

namespace BlockLimiter
{
    public class BlockLimiter : TorchPluginBase, IWpfPlugin
    {
        private PatchManager _pm;
        private PatchContext _context;

        public readonly Logger Log = LogManager.GetLogger("BlockLimiter");
        private Thread _processThread;
        private List<Thread> _processThreads;
        private static bool _running;
        public static BlockLimiter Instance { get; private set; }
        private TorchSessionManager _sessionManager;
        private List<ProcessHandlerBase> _limitHandlers;
        public readonly HashSet<LimitItem> VanillaLimits = new HashSet<LimitItem>();
        
        private int _updateCounter;


        private void DoInit()
        {

            _limitHandlers = new List<ProcessHandlerBase>
            {
                new Annoy(),
                new Punish()
            };
            _processThreads = new List<Thread>();
            _processThread = new Thread(PluginProcessing);
            _processThread.Start();
            
            MyCubeGrids.BlockDestroyed += MyCubeGridsOnBlockDestroyed;
            MyCubeGrid.OnSplitGridCreated += MyCubeGridOnOnSplitGridCreated;
            MyMultiplayer.Static.ClientJoined += StaticOnClientJoined;
            MyCubeGrids.BlockBuilt += MyCubeGridsOnBlockBuilt;
            MySession.Static.Factions.FactionStateChanged += FactionsOnFactionStateChanged;
           // MySession.Static.Factions.FactionEdited += FactionsOnFactionEdited;
        }


        private void FactionsOnFactionStateChanged(MyFactionStateChange factionState, long fromFaction, long toFaction, long playerId, long senderId)
        {
            if (factionState == MyFactionStateChange.RemoveFaction)
            {
                foreach (var limit in BlockLimiterConfig.Instance.AllLimits)
                {
                    limit.Exceptions.Remove(fromFaction.ToString());
                    limit.FoundEntities.Remove(fromFaction);
                }

                return;
            }

            Block.UpdateFactionLimits(fromFaction);
            Block.UpdateFactionLimits(toFaction);
            Block.UpdatePlayerLimits(playerId);

        }

        private void MyCubeGridsOnBlockBuilt(MyCubeGrid grid, MySlimBlock block)
        {
            Block.TryAdd(block, grid);
        }

        private void MyCubeGridOnOnSplitGridCreated(MyCubeGrid obj)
        {
            if (obj == null) return;
            Grid.UpdateLimit(obj);
        }

        private void StaticOnClientJoined(ulong obj)
        {
            var player = MySession.Static.Players.TryGetPlayerBySteamId(obj);
            if (player == null)return;
            Block.UpdatePlayerLimits(player);
        }

        private void MyCubeGridsOnBlockDestroyed(MyCubeGrid arg1, MySlimBlock arg2)
        {
            if (!BlockLimiterConfig.Instance.EnableLimits)return;
            Block.RemoveBlock(arg2);
        }


        private void GetVanillaLimits()
        {
            var limits = new List<LimitItem>(MySession.Static.BlockTypeLimits.Count());

            switch (MySession.Static.BlockLimitsEnabled)
            {
                case MyBlockLimitsEnabledEnum.NONE:
                    break;
                case MyBlockLimitsEnabledEnum.GLOBALLY:
                    break;
                case MyBlockLimitsEnabledEnum.PER_FACTION:
                    limits.AddRange(MySession.Static.BlockTypeLimits.Select(item => new LimitItem
                    {
                        LimitFaction = true,
                        LimitPlayers = false,
                        LimitGrids = false,
                        Limit = item.Value,
                        BlockList = new List<string>{item.Key}
                    }));

                    break;
                case MyBlockLimitsEnabledEnum.PER_PLAYER:
                    limits.AddRange(MySession.Static.BlockTypeLimits.Select(item => new LimitItem
                    {
                        LimitFaction = false,
                        LimitPlayers = true,
                        LimitGrids = false,
                        Limit = item.Value,
                        BlockList = new List<string>{item.Key}
                    }));

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            VanillaLimits.UnionWith(limits);
        }



        private void PluginProcessing()
        {
            try
            {
                foreach (var handler in _limitHandlers)
                {
                    ProcessHandlerBase currentHandler = handler;
                    var thread = new Thread(() =>
                    {
                        while (_running)
                        {
                            if (currentHandler.CanProcess())
                            {
                                try
                                {
                                    currentHandler.Handle();
                                }
                                catch (Exception ex)
                                {
                                    if (BlockLimiterConfig.Instance.EnableLog)
                                        Log.Warn("Handler Problems: {0} - {1}", currentHandler.GetUpdateResolution(),
                                            ex);
                                }

                                currentHandler.LastUpdate = DateTime.Now;
                            }

                            Thread.Sleep(100);
                        }

                    });
                    _processThreads.Add(thread);
                    thread.Start();
                }

                foreach (Thread thread in _processThreads)
                    thread.Join();

            }
            catch (ThreadAbortException ex)
            {
                if (BlockLimiterConfig.Instance.EnableLog) 
                    Log.Trace(ex);
            }
            catch (Exception ex)
            {
                if (BlockLimiterConfig.Instance.EnableLog) 
                    Log.Error(ex);
            }
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            _pm = torch.Managers.GetManager<PatchManager>();
            _context = _pm.AcquireContext();
            Instance = this;
            Load();
            CopyOver();
            _sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (_sessionManager != null)
                _sessionManager.SessionStateChanged += SessionChanged;
            
        }



        public override void Update()
        {
            base.Update();
            if (MyAPIGateway.Session == null|| !BlockLimiterConfig.Instance.EnableLimits)
                return;
            if (++_updateCounter % 100 == 0)
            {
                GridCache.Update();
            }

        }


        private  void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            _running = state == TorchSessionState.Loaded;
            switch (state)
            {
                case TorchSessionState.Loaded:
                    DoInit();
                    EnableControl();
                    GetVanillaLimits();
                    GridCache.Update();
                    Utilities.UpdateLimits(BlockLimiterConfig.Instance.UseVanillaLimits, out BlockLimiterConfig.Instance.AllLimits);
                    ResetLimits();
                    break;
                case TorchSessionState.Unloading:
                    Dispose();
                    break;
            }
        }

        private static void Load()
        {
            BlockLimiterConfig.Instance.Load();
        }
        
        private UserControl _control;
        private UserControl Control => _control ?? (_control = new PropertyGrid{ DataContext = BlockLimiterConfig.Instance});
        public UserControl GetControl()
        {
            return Control;
        }
        private void EnableControl(bool enable = true)
        {
            _control?.Dispatcher?.Invoke(() =>
            {
                Control.IsEnabled = enable;
                Control.DataContext = BlockLimiterConfig.Instance;
            });

        }
        
        /// <summary>
        /// TO Do Remove on next update
        /// </summary>
        private void CopyOver()
        {
            foreach (var limit in BlockLimiterConfig.Instance.LimitItems)
            {
                if (limit.BlockPairName.Count == 0) continue;
                limit.BlockList.AddRange(limit.BlockPairName);
                limit.BlockPairName.Clear();
            }
            BlockLimiterConfig.Instance.Save();

        }

        public override void Dispose()
        {
            base.Dispose();
            _pm.FreeContext(_context);
            foreach (var thread in _processThreads)
                thread.Abort();
            _processThread.Abort();
        }

        public static void ResetLimits()
        {
            var grids = new HashSet<MyCubeGrid>();
           GridCache.GetGrids(grids);
           
           Task.Run(() =>
           {
               Thread.Sleep(300);
               MySandboxGame.Static.Invoke(() =>
               {
                   foreach (var grid in grids)
                   {
                       if (grid == null) continue;
                       Grid.UpdateLimit(grid);
                   }
               }, "BlockLimiter");
           });
           
           Task.Run(() =>
           {
               Thread.Sleep(100);
               MySandboxGame.Static.Invoke(() =>
               {
                   foreach (var player in MySession.Static.Players.GetAllPlayers())
                   {
                       var identity = Utilities.GetPlayerIdFromSteamId(player.SteamId);
                       if (identity < 1) continue;
                       Block.UpdatePlayerLimits(identity);
                   }
               }, "BlockLimiter");
           });
           
           Task.Run(() =>
           {
               Thread.Sleep(500);
               MySandboxGame.Static.Invoke(() =>
               {

                   foreach (var id in MySession.Static.Factions.Factions.Keys)
                   {
                       Block.UpdateFactionLimits(id);
                   }
               }, "BlockLimiter");
           });


        }

        
        public static bool CheckLimits_future(MyObjectBuilder_CubeGrid[] grids)
        {

            if (!BlockLimiterConfig.Instance.EnableLimits)
            {
                return false;
            }

            return !grids.Any(Grid.IsSizeViolation) && 
                   !grids.Any(z=>z.CubeBlocks.Any(b=>Block.AllowBlock(MyDefinitionManager.Static.GetCubeBlockDefinition(b),0,z)));
        }
        
        

        
    }


    }
