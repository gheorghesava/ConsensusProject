﻿using ConsensusProject.Abstractions;
using ConsensusProject.Messages;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ConsensusProject.App
{
    public class AppSystem
    {
        private Config _config;
        private AppLogger _logger;
        private ConcurrentDictionary<string, Abstraction> _abstractions = new ConcurrentDictionary<string, Abstraction>();
        private AppProccess _appProccess;
        public string SystemId { get; private set; }

        public AppSystem(string systemId, Config config, AppProccess appProccess)
        {
            SystemId = systemId;
            _config = config;
            _logger = new AppLogger(_config, "AppSystem", SystemId);
            _appProccess = appProccess;
            _initializeAbstractions(this);

            new Thread(() => EventLoop()).Start();
        }

        public bool Decided => ((UniformConsensus)_abstractions["uc"]).Decided;

        public void EventLoop()
        {
            while (!Decided)
            {
                foreach(var message in _appProccess.Messages)
                {
                    if (message.SystemId == SystemId)
                    {
                        foreach(Abstraction abstraction in _abstractions.Values)
                        {
                            if (abstraction.Handle(message))
                            {
                                _appProccess.DequeMessage(message);
                            }
                        }
                    }
                }
            }
        }

        public ProcessId CurrentProccess
        {
            get
            {
                return _appProccess.ShardNodes.Find(it => _config.IsEqual(it));
            }
        }

        public void InitializeNewEpochConsensus(int ets, EpState_ state)
        {
            if (!_abstractions.TryAdd($"ep{ets}", new EpochConsensus($"ep{ets}", _config, _appProccess, this, ets, state)))
                throw new System.Exception($"Error adding a new epoch consensus with timestamp {ets}.");
        }

        public int NrOfProcesses
        {
            get
            {
                return _appProccess.ShardNodes.Count;
            }
        }

        #region Private methods
        private void _initializeAbstractions(AppSystem appSystem)
        {
            _abstractions.TryAdd( "ec", new EpochChange("ec", _config, _appProccess, appSystem));
            _abstractions.TryAdd( "uc", new UniformConsensus("uc", _config, _appProccess, appSystem));
        }
        #endregion Private methods
    }
}
