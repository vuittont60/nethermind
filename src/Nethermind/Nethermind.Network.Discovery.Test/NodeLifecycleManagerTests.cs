﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeLifecycleManagerTests
    {
        private Signature[] _signatureMocks = Array.Empty<Signature>();
        private PublicKey[] _nodeIds = Array.Empty<PublicKey>();
        private INodeStats _nodeStatsMock = null!;

        private readonly INetworkConfig _networkConfig = new NetworkConfig();
        private IDiscoveryManager _discoveryManager = null!;
        private IDiscoveryManager _discoveryManagerMock = null!;
        private IDiscoveryConfig _discoveryConfigMock = null!;
        private INodeTable _nodeTable = null!;
        private IEvictionManager _evictionManagerMock = null!;
        private ILogger _loggerMock = null!;
        private int _port = 1;
        private string _host = "192.168.1.27";
        
        [SetUp]
        public void Setup()
        {
            _discoveryManagerMock = Substitute.For<IDiscoveryManager>();
            _discoveryConfigMock = Substitute.For<IDiscoveryConfig>();

            NetworkNodeDecoder.Init();
            SetupNodeIds();

            LimboLogs? logManager = LimboLogs.Instance;
            _loggerMock = Substitute.For<ILogger>();
            //setting config to store 3 nodes in a bucket and for table to have one bucket//setting config to store 3 nodes in a bucket and for table to have one bucket

            IConfigProvider configurationProvider = new ConfigProvider();
            _networkConfig.ExternalIp = "99.10.10.66";
            _networkConfig.LocalIp = "10.0.0.5";
            
            IDiscoveryConfig discoveryConfig = configurationProvider.GetConfig<IDiscoveryConfig>();
            discoveryConfig.PongTimeout = 50;
            discoveryConfig.BucketSize = 3;
            discoveryConfig.BucketsCount = 1;

            NodeDistanceCalculator calculator = new(discoveryConfig);

            _nodeTable = new NodeTable(calculator, discoveryConfig, _networkConfig, logManager);
            _nodeTable.Initialize(TestItem.PublicKeyA);
            _nodeStatsMock = Substitute.For<INodeStats>();
            
            EvictionManager evictionManager = new(_nodeTable, logManager);
            _evictionManagerMock = Substitute.For<IEvictionManager>();
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeLifecycleManagerFactory lifecycleFactory = new(_nodeTable, evictionManager, 
                new NodeStatsManager(timerFactory, logManager), new NodeRecord(), discoveryConfig, Timestamper.Default, logManager);

            IMsgSender udpClient = Substitute.For<IMsgSender>();

            SimpleFilePublicKeyDb discoveryDb = new("Test","test", logManager);
            _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeTable, new NetworkStorage(discoveryDb, logManager), discoveryConfig, logManager);
            _discoveryManager.MsgSender = udpClient;

            _discoveryManagerMock = Substitute.For<IDiscoveryManager>();
        }

        [Test]
        public async Task sending_ping_receiving_proper_pong_sets_bounded()
        {
            Node node = new(TestItem.PublicKeyB, _host, _port);
            NodeLifecycleManager nodeManager = new(node, _discoveryManagerMock
            , _nodeTable, _evictionManagerMock, _nodeStatsMock, new NodeRecord(), _discoveryConfigMock, Timestamper.Default, _loggerMock);

            byte[] mdc = new byte[32];
            PingMsg? sentPing = null;
            _discoveryManagerMock.SendMessage(Arg.Do<PingMsg>(msg =>
            {
                msg.Mdc = mdc;
                sentPing = msg;
            }));

            await nodeManager.SendPingAsync();
            nodeManager.ProcessPongMsg(new PongMsg(node.Address, GetExpirationTime(), sentPing!.Mdc!));

            Assert.IsTrue(nodeManager.IsBonded);
        }
        
        [Test]
        public async Task sending_ping_receiving_incorrect_pong_does_not_bond()
        {
            Node node = new(TestItem.PublicKeyB, _host, _port);
            NodeLifecycleManager nodeManager = new(node, _discoveryManagerMock
            , _nodeTable, _evictionManagerMock, _nodeStatsMock, new NodeRecord(), _discoveryConfigMock, Timestamper.Default, _loggerMock);

            await nodeManager.SendPingAsync();
            nodeManager.ProcessPongMsg(new PongMsg(TestItem.PublicKeyB, GetExpirationTime(), new byte[] {1,1,1}));

            Assert.IsFalse(nodeManager.IsBonded);
        }

        [Test]
        public void Wrong_pong_will_get_ignored()
        {
            Node node = new(TestItem.PublicKeyB, _host, _port);
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager?.State);
            
            PongMsg msgI = new (_nodeIds[0], GetExpirationTime(), new byte[32]);
            msgI.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
            _discoveryManager.OnIncomingMsg(msgI);
            
            Assert.AreEqual(NodeLifecycleState.New, manager?.State);
        }

        [Test]
        [Retry(3)]
        public async Task UnreachableStateTest()
        {
            Node node = new(TestItem.PublicKeyB, _host, _port);
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager?.State);

            await Task.Delay(500);

            Assert.That(() => manager?.State, Is.EqualTo(NodeLifecycleState.Unreachable).After(500, 50));
            //Assert.AreEqual(NodeLifecycleState.Unreachable, manager.State);
        }

        [Test, Retry(3), Ignore("Eviction changes were introduced and we would need to expose some internals to test bonding")]
        public void EvictCandidateStateWonEvictionTest()
        {
            //adding 3 active nodes
            List<INodeLifecycleManager> managers = new();
            for (int i = 0; i < 3; i++)
            {
                string host = "192.168.1." + i;
                Node node = new(_nodeIds[i], host, _port);
                INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
                if (manager is null)
                {
                    throw new Exception("Manager is null");
                }
                
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                PongMsg msgI = new (_nodeIds[i], GetExpirationTime(), new byte[32]);
                msgI.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
                _discoveryManager.OnIncomingMsg(msgI);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);
            }

            //table should contain 3 active nodes
            IEnumerable<Node> closestNodes = _nodeTable.GetClosestNodes().ToArray();
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 0);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 0);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 0);

            //adding 4th node - table can store only 3, eviction process should start
            Node candidateNode = new(_nodeIds[3], _host, _port);
            INodeLifecycleManager? candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);

            Assert.AreEqual(NodeLifecycleState.New, candidateManager?.State);

            PongMsg pongMsg = new (_nodeIds[3], GetExpirationTime(), new byte[32]);
            pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
            _discoveryManager.OnIncomingMsg(pongMsg);
            
            Assert.AreEqual(NodeLifecycleState.New, candidateManager?.State);
            INodeLifecycleManager evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);

            //receiving pong for eviction candidate - should survive
            PongMsg msg = new (evictionCandidate.ManagedNode.Id, GetExpirationTime(), new byte[32]);
            msg.FarAddress = new IPEndPoint(IPAddress.Parse(evictionCandidate.ManagedNode.Host), _port);
            _discoveryManager.OnIncomingMsg(msg);
            
            //await Task.Delay(100);

            //3th node should survive, 4th node should be active but not in the table
            Assert.That(() => candidateManager?.State, Is.EqualTo(NodeLifecycleState.ActiveExcluded).After(100, 50));
            Assert.That(() => evictionCandidate.State, Is.EqualTo(NodeLifecycleState.Active).After(100, 50));

            //Assert.AreEqual(NodeLifecycleState.ActiveExcluded, candidateManager.State);
            //Assert.AreEqual(NodeLifecycleState.Active, evictionCandidate.State);
            closestNodes = _nodeTable.GetClosestNodes();
            Assert.That(() => closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1, Is.True.After(100, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1, Is.True.After(100, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1, Is.True.After(100, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == candidateNode.Host) == 0, Is.True.After(100, 50));
            
            //Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == candidateNode.Host) == 0);
        }
        
        private static long GetExpirationTime() => Timestamper.Default.UnixTime.SecondsLong + 20;

        [Test]
        [Ignore("This test keeps failing and should be only manually enabled / understood when we review the discovery code")]
        public void EvictCandidateStateLostEvictionTest()
        {
            //adding 3 active nodes
            List<INodeLifecycleManager> managers = new();
            for (int i = 0; i < 3; i++)
            {
                string host = "192.168.1." + i;
                Node node = new(_nodeIds[i], host, _port);
                INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
                if (manager is null)
                {
                    throw new Exception("Manager is null");
                }
                
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                PongMsg msg = new (_nodeIds[i], GetExpirationTime(), new byte[32]);
                msg.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
                _discoveryManager.OnIncomingMsg(msg);
                
                Assert.AreEqual(NodeLifecycleState.Active, manager.State);
            }

            //table should contain 3 active nodes
            IEnumerable<Node> closestNodes = _nodeTable.GetClosestNodes().ToArray();
            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            }

            //adding 4th node - table can store only 3, eviction process should start
            Node candidateNode = new(_nodeIds[3], _host, _port);

            INodeLifecycleManager? candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);
            Assert.AreEqual(NodeLifecycleState.New, candidateManager?.State);

            PongMsg pongMsg = new (_nodeIds[3], GetExpirationTime(), new byte[32]);
            pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
            _discoveryManager.OnIncomingMsg(pongMsg);

            //await Task.Delay(10);
            Assert.That(() => candidateManager?.State, Is.EqualTo(NodeLifecycleState.Active).After(10, 5));
            //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);

            INodeLifecycleManager evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);
            //await Task.Delay(300);

            //3th node should be evicted, 4th node should be added to the table
            //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            Assert.That(() => candidateManager?.State, Is.EqualTo(NodeLifecycleState.Active).After(300, 50));
            //Assert.AreEqual(NodeLifecycleState.Unreachable, evictionCandidate.State);
            Assert.That(() => evictionCandidate.State, Is.EqualTo(NodeLifecycleState.Unreachable).After(300, 50));

            closestNodes = _nodeTable.GetClosestNodes();
            Assert.That(() => managers.Where(x => x.State == NodeLifecycleState.Active).All(x => closestNodes.Any(y => y.Host == x.ManagedNode.Host)), Is.True.After(300, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == evictionCandidate.ManagedNode.Host) == 0, Is.True.After(300, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == candidateNode.Host) == 1, Is.True.After(300, 50));

            //Assert.IsTrue(managers.Where(x => x.State == NodeLifecycleState.Active).All(x => closestNodes.Any(y => y.Host == x.ManagedNode.Host)));
            //Assert.IsTrue(closestNodes.Count(x => x.Host == evictionCandidate.ManagedNode.Host) == 0);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == candidateNode.Host) == 1);
        }

        private void SetupNodeIds()
        {
            _signatureMocks = new Signature[4];
            _nodeIds = new PublicKey[4];

            for (int i = 0; i < 4; i++)
            {
                byte[] signatureBytes = new byte[65];
                signatureBytes[64] = (byte)i;
                _signatureMocks[i] = new Signature(signatureBytes);

                byte[] nodeIdBytes = new byte[64];
                nodeIdBytes[63] = (byte)i;
                _nodeIds[i] = new PublicKey(nodeIdBytes);
            }
        }
    }
}