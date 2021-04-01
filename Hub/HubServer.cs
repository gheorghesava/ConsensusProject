﻿using ConsensusProject.App;
using ConsensusProject.Messages;
using ConsensusProject.Utils;
using Google.Protobuf.Collections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Hub
{
    public class ShardItem
    {
        public string Alias { get; set; }
        public int StartPort { get; set; }
        public int EndPort { get; set; }
    }

    public class TransactionReported
    {
        public Transaction Transaction { get; set; }
        public Stopwatch StopWatch { get; set; } = new Stopwatch();
    }

    public class HubServer
    {
        private Dictionary<string, TransactionReported> transactions = new Dictionary<string, TransactionReported>();
        private List<ProcessId> _processes = new List<ProcessId>();
        private MessageBroker _broker;
        private AppLogger _logger;
        private Config _config;

        public HubServer(Config config)
        {
            _config = config;
            _logger = new AppLogger(config, "hub");
            _broker = new MessageBroker(config);
            new Thread(() =>
            {
                HandleMessages();
            }).Start();
        }


        public void Run()
        {
            while (true)
            {
                Console.Write("command> ");
                string cmd = Console.ReadLine();

                string[] cmdList = cmd.Split(new char[] { ' ' }, 2);

                switch (cmdList[0])
                {
                    case "help":
                        PrintMainMenu();
                        break;
                    case "transfer":
                        MakeTransaction(cmdList[1]);
                        break;
                    case "deposit":
                        MakeTransaction(cmdList[1], isDeposit: true);
                        break;
                    case "transactions":
                        PrintTransactions();
                        break;
                    case "nodes":
                        ListAllNodes();
                        break;
                    case "deploy":
                        Deploy(cmdList[1]);
                        break;
                    case "stop":
                        Stop(cmdList[1]);
                        break;
                    default:
                        Console.WriteLine("Incorrect command");
                        break;
                }
            }
        }

        public void Deploy(string argsString)
        {
            try
            {
                var shards = DecomposeShardArgs(argsString);

                Message deploy = new Message
                {
                    MessageUuid = NewId,
                    Type = Message.Types.Type.NetworkMessage,
                    SystemId = NewId,
                    NetworkMessage = new NetworkMessage
                    {
                        SenderHost = _config.HubIpAddress,
                        SenderListeningPort = _config.HubPort,
                        Message = new Message
                        {
                            MessageUuid = NewId,
                            SystemId = NewId,
                            Type = Message.Types.Type.DeployNodes,
                            DeployNodes = new DeployNodes()
                        }
                    }
                };

                AssignShardProcessesToMessage(shards, deploy.NetworkMessage.Message.DeployNodes.Processes);

                _broker.SendMessage(deploy, _config.NodeHandlerIpAddress, _config.NodeHandlerPort);
            }
            catch
            {
                Console.WriteLine("Try again!");
            }
        }

        public void Stop(string argsString)
        {
            try
            {
                var shards = DecomposeShardArgs(argsString);

                Message stop = new Message
                {
                    MessageUuid = NewId,
                    Type = Message.Types.Type.NetworkMessage,
                    SystemId = NewId,
                    NetworkMessage = new NetworkMessage
                    {
                        SenderHost = _config.HubIpAddress,
                        SenderListeningPort = _config.HubPort,
                        Message = new Message
                        {
                            MessageUuid = NewId,
                            SystemId = NewId,
                            Type = Message.Types.Type.StopNodes,
                            StopNodes = new StopNodes()
                        }
                    }
                };

                AssignShardProcessesToMessage(shards, stop.NetworkMessage.Message.StopNodes.Processes);

                _broker.SendMessage(stop, _config.NodeHandlerIpAddress, _config.NodeHandlerPort);
            }
            catch (Exception)
            {
                Console.WriteLine("Try again!");
            }
        }

        private void MakeTransaction(string argsString, bool isDeposit = false)
        {
            var args = new Dictionary<string, string>();
            argsString
                .Trim().Split("-")
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .ToList()
                .ConvertAll(it => it.Trim().Split())
                .ForEach(it => args[it[0]] = it[1]);

            var dstAccount = args["to"];
            var srcAccount = string.Empty;
            if (!isDeposit) 
                srcAccount = args["from"];
            double amount = double.Parse(args["a"]);
            var shard = args["s"];

            var appPropose = new AppPropose
            {
                Value = new Value
                {
                    Defined = true,
                    UnixEpoch = UnixEpoch,
                    Transaction = new Transaction
                    {
                        Id = NewId,
                        From = srcAccount,
                        To = dstAccount,
                        Amount = amount,
                        Shard = shard
                    }
                },
            };

            var txReported = new TransactionReported { Transaction = appPropose.Value.Transaction };
            transactions.Add(txReported.Transaction.Id, txReported);

            var shardProcesses = _processes.Where(it => it.Owner == shard).ToList();
            appPropose.Processes.AddRange(shardProcesses);
            Message deposit = new Message
            {
                MessageUuid = NewId,
                Type = Message.Types.Type.NetworkMessage,
                SystemId = NewId,
                NetworkMessage = new NetworkMessage
                {
                    SenderHost = _config.HubIpAddress,
                    SenderListeningPort = _config.HubPort,
                    Message = new Message
                    {
                        MessageUuid = NewId,
                        SystemId = NewId,
                        Type = Message.Types.Type.AppPropose,
                        AppPropose = appPropose
                    }
                }
            };

            txReported.StopWatch.Start();
            foreach (var process in shardProcesses)
            {
                _logger.LogInfo($"Process {process.Owner}-{process.Index} will propose transaction Id={appPropose.Value.Transaction.Id}");
                _broker.SendMessage(deposit, process.Host, process.Port);
            }
        }

        private void HandleMessages()
        {
            while (true)
            {
                if (_broker.Messages.Count == 0)
                {
                    Thread.Sleep(1000);
                    continue;
                }

                var msg = _broker.Messages.First();

                switch (msg.NetworkMessage.Message.Type)
                {
                    case Message.Types.Type.AppDecide:
                        var process = _processes.Find(it => it.Host == msg.NetworkMessage.SenderHost && it.Port == msg.NetworkMessage.SenderListeningPort);
                        _logger.LogInfo($"Transaction Id={msg.NetworkMessage.Message.AppDecide.Value.Transaction.Id} accepted by {process.Owner}-{process.Index}");
                        transactions[msg.NetworkMessage.Message.AppDecide.Value.Transaction.Id].StopWatch.Stop();
                        break;
                    case Message.Types.Type.AppRegistration:
                        RegisterProcess(msg);
                        break;
                    default:
                        Console.WriteLine("Unhandled message!");
                        break;
                }

                _broker.DequeMessage(msg);
            }
        }

        private void RegisterProcess(Message message)
        {
            var newProcess = new ProcessId
            {
                Host = message.NetworkMessage.SenderHost,
                Port = message.NetworkMessage.SenderListeningPort,
                Owner = message.NetworkMessage.Message.AppRegistration.Owner,
                Index = message.NetworkMessage.Message.AppRegistration.Index,
                Rank = _processes.Count
            };

            var node = _processes.FirstOrDefault(n => n.Owner == newProcess.Owner && n.Index == newProcess.Index);
            if (node == null)
            {
                _processes.Add(newProcess);
                _logger.LogInfo($"{newProcess.Owner}-{newProcess.Port}: listening to {newProcess.Host}:{newProcess.Port}");
            }
            else
                _logger.LogInfo($"{newProcess.Owner}-{newProcess.Port}: already registered");
        }

        private void AssignShardProcessesToMessage(List<ShardItem> shards, RepeatedField<ProcessId> processes)
        {
            foreach (var shard in shards)
            {
                if (shard.StartPort > shard.EndPort)
                {
                    Console.WriteLine($"Wrong arguments for shard '{shard.Alias}': ports must pe positive and start port lower or equal to end port!");
                    continue;
                }

                bool doesOverlap = shards.TrueForAll(it => it.Alias != shard.Alias && !(it.EndPort < shard.StartPort || shard.EndPort < it.StartPort));

                if (doesOverlap)
                {
                    Console.WriteLine($"Shard '{shard.Alias}' is overlapping with one of the other shards!");
                    continue;
                }

                var index = 0;
                for (int port = shard.StartPort; port <= shard.EndPort; port++)
                {
                    var newNode = new ProcessId
                    {
                        Host = _config.HubIpAddress,
                        Port = port,
                        Owner = shard.Alias,
                        Index = index
                    };

                    processes.Add(newNode);
                    index++;
                }
            }
        }

        private void PrintTransactions()
        {
            var output = "\n-----------TRANSACTIONS----------\n";
            output += transactions.ToList().ToStringTable(
                new string[] { "TRANSACTION ID", "SOURCE ACCOUNT", "DESTINATION ACCOUNT", "AMOUNT", "TIME ELAPSED" },
                p => p.Value.Transaction.Id, p => p.Value.Transaction.From, p => p.Value.Transaction.To, p => p.Value.Transaction.Amount, p => p.Value.StopWatch.IsRunning ? "WAITING" : p.Value.StopWatch.Elapsed.ToString()
                );
            Console.WriteLine(output);
        }

        private List<ShardItem> DecomposeShardArgs(string args) =>
            args
                .Split("-s")
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .ToList()
                .ConvertAll(it => it.Trim().Split())
                .ConvertAll(it => new ShardItem
                {
                    Alias = it[0],
                    StartPort = int.Parse(it[1].Split("-")[0]),
                    EndPort = int.Parse(it[1].Split("-")[1])
                });

        private string NewId { get { return Guid.NewGuid().ToString().Substring(0, 5); } }

        private long UnixEpoch { get { return ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds(); } }

        private void ListAllNodes()
        {
            string table = _processes.ToStringTable(
                new string[] { "HOST", "PORT", "OWNER", "INDEX", "RANK"},
                it => it.Host, it => it.Port, it => it.Owner, it => it.Index, it => it.Rank);
            Console.WriteLine(table);
        }

        private void PrintMainMenu()
        {
            string menu = @"
    help
    nodes
    transactions
    transfer -from <nickname> -to <nickname> -a <amount> -s <alias>
    deposit -to <nickname> -a <amount> -s <alias>
    deploy -s <alias> <port>-<port> ...
    stop -s <alias> <port>-<port> ...
            ";

            Console.WriteLine(menu);
        }
    }
}
