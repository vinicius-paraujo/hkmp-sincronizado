using Hkmp.Game.Client.Entity;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace Sincroniza.Barrier.Tests {
    public class EntityChildStateTests {
        [Fact]
        public void A_disabled_replica_root_preserves_its_active_damage_box_and_inactive_snore() {
            var scene = new Scene("Crossroads_04"); var host = new GameObject("Giant Fly", scene); var client = new GameObject("Giant Fly(Clone)", scene);
            var damage = host.Child("Hero Damager", false); var snore = host.Child("Snore", true);
            client.Child("Hero Damager", true).activeInHierarchy = false; client.Child("Snore", false); client.SetActive(false);
            EntityChildState.Restore(host, client);
            Assert.True(damage.activeSelf); Assert.False(snore.activeSelf);
        }
        [Fact]
        public void Registered_children_keep_their_own_network_ownership() {
            var scene = new Scene("Room"); var host = new GameObject("Host", scene); var client = new GameObject("Client", scene);
            var managed = host.Child("Enemy", false); managed.Managed = true; client.Child("Enemy", true);
            EntityChildState.Restore(host, client); Assert.False(managed.activeSelf);
        }
        [Fact]
        public void Nested_hitboxes_and_duplicate_names_preserve_each_individual_state() {
            var scene = new Scene("Room"); var host = new GameObject("Host", scene); var client = new GameObject("Client", scene);
            var first = host.Child("Arm", true).Child("Hitbox", false); var second = host.Child("Arm", false);
            client.Child("Arm", false).Child("Hitbox", true); client.Child("Arm", true); client.Child("Particle without original", true);
            EntityChildState.Restore(host, client); Assert.True(first.activeSelf); Assert.True(second.activeSelf);
            Assert.False(host.transform.Children[0].gameObject.activeSelf);
        }
    }
}
