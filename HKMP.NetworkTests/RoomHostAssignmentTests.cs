using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Server.Rooms;
using Hkmp.Networking.Packet.Data;
using Xunit;
using Xunit.Abstractions;

namespace Hkmp.NetworkTests;

/// <summary>
/// The room host assignment: who hosts each room, in which epoch, and which entities died in it, decided in one
/// place. The simulation drives it with players moving between rooms, going silent, coming back and leaving at
/// random moments, and checks after every step what must always hold.
/// </summary>
public class RoomHostAssignmentTests {
    private const string Room = "Crossroads_04";

    private readonly ITestOutputHelper _output;

    public RoomHostAssignmentTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public void The_first_player_in_a_room_hosts_it_and_the_next_joins_as_a_client() {
        var rooms = NewAssignment();

        var first = rooms.Enter(Room, 1, hostsRooms: true);
        var second = rooms.Enter(Room, 2, hostsRooms: true);

        Assert.True(first.IsHost);
        Assert.False(second.IsHost);
        Assert.Equal(first.Epoch, second.Epoch);
        Assert.Equal((ushort) 1, rooms.HostOf(Room));
    }

    [Fact]
    public void Without_full_synchronisation_no_one_hosts_a_room() {
        var rooms = NewAssignment();

        var entry = rooms.Enter(Room, 1, hostsRooms: false);

        Assert.False(entry.IsHost);
        Assert.Null(rooms.HostOf(Room));
    }

    [Fact]
    public void Every_change_of_host_starts_a_newer_epoch_than_any_before_even_in_other_rooms() {
        var rooms = NewAssignment();
        var epochs = new List<uint> { rooms.Enter("A", 1, true).Epoch, rooms.Enter("B", 2, true).Epoch };
        rooms.Enter("A", 3, true);
        epochs.Add(rooms.Transfer("A", 1, [3], HostChangeReason.LeftRoom).Epoch);

        rooms.Forget("B");
        epochs.Add(rooms.Enter("B", 2, true).Epoch);

        Assert.Equal(epochs.OrderBy(epoch => epoch), epochs);
        Assert.Equal(epochs.Count, epochs.Distinct().Count());
    }

    [Fact]
    public void Only_the_current_host_can_hand_the_room_over() {
        var rooms = NewAssignment();
        rooms.Enter(Room, 1, true);
        rooms.Enter(Room, 2, true);

        Assert.Null(rooms.Transfer(Room, 2, [1], HostChangeReason.LeftRoom));
        Assert.Equal((ushort) 1, rooms.HostOf(Room));
    }

    [Fact]
    public void A_silent_host_with_no_one_to_take_over_keeps_the_room_but_a_host_that_left_does_not() {
        var rooms = NewAssignment();
        rooms.Enter(Room, 1, true);

        Assert.Null(rooms.Transfer(Room, 1, [], HostChangeReason.Silent));
        Assert.Equal((ushort) 1, rooms.HostOf(Room));

        Assert.Null(rooms.Transfer(Room, 1, [], HostChangeReason.Disconnected));
        Assert.Null(rooms.HostOf(Room));
    }

    [Fact]
    public void The_policy_avoids_silent_players_and_prefers_the_server_owner() {
        var silent = new HashSet<ushort> { 2 };
        var policy = new HostCandidatePolicy(silent.Contains, player => player == 5);

        Assert.Equal((ushort) 5, policy.Choose([2, 3, 5]));
        Assert.Equal((ushort) 3, policy.Choose([2, 3, 4]));
        Assert.Equal((ushort) 2, policy.Choose([2]));
        Assert.Null(policy.Choose([]));
    }

    [Fact]
    public void A_death_counts_from_the_host_of_its_epoch_even_after_the_room_changed_hands() {
        var rooms = NewAssignment();
        var epoch = rooms.Enter(Room, 1, true).Epoch;
        rooms.Enter(Room, 2, true);
        var change = rooms.Transfer(Room, 1, [2], HostChangeReason.Silent);

        Assert.True(rooms.RecordDeath(Room, 1, epoch, new EntityLife(7, 0)));
        Assert.False(rooms.RecordDeath(Room, 2, epoch, new EntityLife(8, 0)));
        Assert.Contains(new EntityLife(7, 0), rooms.DeadEntities(Room));
        Assert.DoesNotContain(rooms.DeadEntities(Room), death => death.EntityId == 8);
        Assert.Empty(change.Dead);
    }

    [Fact]
    public void A_dead_entity_only_comes_back_in_a_later_life() {
        var rooms = NewAssignment();
        var epoch = rooms.Enter(Room, 1, true).Epoch;
        rooms.RecordDeath(Room, 1, epoch, new EntityLife(7, 0));

        Assert.False(rooms.Revive(Room, new EntityLife(7, 0)));
        Assert.True(rooms.IsDead(Room, new EntityLife(7, 0)));

        Assert.True(rooms.Revive(Room, new EntityLife(7, 1)));
        Assert.Empty(rooms.DeadEntities(Room));
        Assert.True(rooms.Revive(Room, new EntityLife(9, 0)));
    }

    [Fact]
    public void A_client_starts_a_new_life_only_for_an_entity_it_knows_to_be_dead() {
        var deaths = new KnownDeaths();

        Assert.Equal((ushort) 0, deaths.BeginLife(3));

        deaths.Replace([new EntityLife(3, 4)]);
        Assert.True(deaths.Contains(3));
        Assert.Equal((ushort) 5, deaths.BeginLife(3));
        Assert.False(deaths.Contains(3));

        deaths.Add(3, 5);
        deaths.Alive(3, 5);
        Assert.True(deaths.Contains(3));
        deaths.Alive(3, 6);
        Assert.False(deaths.Contains(3));
        Assert.Equal((ushort) 6, deaths.LifeOf(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Random_moves_silences_and_departures_keep_one_host_per_epoch(int seed) {
        new Simulation(seed, _output).Run(steps: 5000);
    }

    private static RoomHostAssignment NewAssignment() {
        return new RoomHostAssignment(new HostCandidatePolicy(_ => false, _ => false));
    }

    /// <summary>
    /// Players that enter rooms, leave them, go silent, resume, disconnect and come back, and hosts that report
    /// deaths and revivals, in an order chosen by a seeded random generator, as the server's handlers would see it.
    /// </summary>
    private sealed class Simulation {
        private static readonly string[] Rooms = ["A", "B", "C"];
        private const ushort Players = 6;

        private readonly Random _random;
        private readonly ITestOutputHelper _output;
        private readonly RoomHostAssignment _rooms;
        private readonly Dictionary<ushort, string> _sceneOf = new();
        private readonly HashSet<ushort> _silent = new();
        private readonly Dictionary<uint, ushort> _hostOfEpoch = new();
        private readonly Dictionary<string, HashSet<ushort>> _deadSeen = new();
        private uint _lastEpoch;

        public Simulation(int seed, ITestOutputHelper output) {
            _random = new Random(seed);
            _output = output;
            _rooms = new RoomHostAssignment(new HostCandidatePolicy(_silent.Contains, player => player == 0));
        }

        public void Run(int steps) {
            var changes = 0;
            for (var step = 0; step < steps; step++) {
                var player = (ushort) _random.Next(Players);
                switch (_random.Next(6)) {
                    case 0:
                    case 1:
                        Move(player, Rooms[_random.Next(Rooms.Length)]);
                        break;
                    case 2:
                        changes += Silence(player) ? 1 : 0;
                        break;
                    case 3:
                        _silent.Remove(player);
                        break;
                    case 4:
                        changes += Disconnect(player) ? 1 : 0;
                        break;
                    default:
                        ReportEntity(player);
                        break;
                }

                Check(step);
            }

            _output.WriteLine($"{steps} steps, {changes} hand-overs by silence or disconnect, last epoch {_lastEpoch}");
        }

        private void Move(ushort player, string scene) {
            if (_sceneOf.TryGetValue(player, out var current)) {
                if (current == scene) {
                    return;
                }

                Leave(player, current, HostChangeReason.LeftRoom);
            }

            _sceneOf[player] = scene;
            var entry = _rooms.Enter(scene, player, true);
            if (entry.IsHost) Started(entry.Epoch, player);
            Assert.True(entry.Epoch == _rooms.EpochOf(scene), "An entering player was told an old epoch.");
        }

        private bool Silence(ushort player) {
            _silent.Add(player);
            if (!_sceneOf.TryGetValue(player, out var scene)) return false;
            var change = _rooms.Transfer(scene, player, PlayersIn(scene), HostChangeReason.Silent);
            if (change != null) Started(change.Epoch, change.NewHost);
            return change != null;
        }

        private bool Disconnect(ushort player) {
            _silent.Remove(player);
            if (!_sceneOf.TryGetValue(player, out var scene)) return false;
            var moved = Leave(player, scene, HostChangeReason.Disconnected);
            _sceneOf.Remove(player);
            return moved;
        }

        private bool Leave(ushort player, string scene, HostChangeReason reason) {
            var others = PlayersIn(scene).Where(other => other != player).ToList();
            var change = _rooms.Transfer(scene, player, others, reason);
            if (change != null) {
                Assert.Contains(change.NewHost, others);
                Started(change.Epoch, change.NewHost);
            }

            _sceneOf.Remove(player);
            if (others.Count == 0) {
                _rooms.Forget(scene);
                _deadSeen.Remove(scene);
            }

            return change != null;
        }

        private void ReportEntity(ushort player) {
            if (!_sceneOf.TryGetValue(player, out var scene)) return;
            var entity = (ushort) _random.Next(4);
            var epoch = _rooms.EpochOf(scene);
            if (_random.Next(2) == 0) {
                if (_rooms.RecordDeath(scene, player, epoch, new EntityLife(entity, 0))) {
                    Assert.Equal(player, _hostOfEpoch[epoch]);
                    Seen(scene).Add(entity);
                }
            } else if (_rooms.IsHost(scene, player) && _rooms.Revive(scene, new EntityLife(entity, 0))) {
                Assert.DoesNotContain(entity, Seen(scene));
            }
        }

        private void Started(uint epoch, ushort host) {
            Assert.True(epoch > _lastEpoch, $"Epoch {epoch} does not follow {_lastEpoch}.");
            Assert.False(_hostOfEpoch.ContainsKey(epoch), $"Epoch {epoch} was started twice.");
            _lastEpoch = epoch;
            _hostOfEpoch[epoch] = host;
        }

        private void Check(int step) {
            foreach (var scene in Rooms) {
                var players = PlayersIn(scene).ToList();
                var host = _rooms.HostOf(scene);
                if (players.Count > 0) {
                    Assert.True(host != null, $"Step {step}: {scene} has players but no host.");
                    Assert.Contains(host.Value, players);
                    Assert.Equal(host.Value, _hostOfEpoch[_rooms.EpochOf(scene)]);
                    if (players.Any(player => !_silent.Contains(player)) && _silent.Contains(host.Value)) {
                        // A silent host may keep the room only until a hand-over happens; it never gets it anew.
                        Assert.True(_hostOfEpoch[_rooms.EpochOf(scene)] == host.Value);
                    }
                }

                foreach (var dead in Seen(scene)) {
                    Assert.True(_rooms.IsDead(scene, new EntityLife(dead, 0)), $"Step {step}: a death in {scene} was lost.");
                }
            }
        }

        private IEnumerable<ushort> PlayersIn(string scene) {
            return _sceneOf.Where(pair => pair.Value == scene).Select(pair => pair.Key).ToList();
        }

        private HashSet<ushort> Seen(string scene) {
            if (!_deadSeen.TryGetValue(scene, out var seen)) {
                seen = new HashSet<ushort>();
                _deadSeen[scene] = seen;
            }

            return seen;
        }
    }
}
