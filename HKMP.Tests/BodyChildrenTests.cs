using Hkmp.Game.Client.Entity;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace Sincroniza.Barrier.Tests {
    public class BodyChildrenTests {
        [Fact]
        public void A_hitbox_switched_on_by_an_earlier_state_is_switched_on_again() {
            var scene = new Scene("Crossroads_04");
            var host = new GameObject("Giant Fly", scene); var original = new GameObject("Giant Fly", scene);
            host.Child("Hero Damager", true); host.Child("Snore", false);
            var damage = original.Child("Hero Damager", false); var snore = original.Child("Snore", true);
            Assert.Equal(0, BodyChildren.Apply(original, BodyChildren.Read(host)));
            Assert.True(damage.activeSelf); Assert.False(snore.activeSelf);
        }

        [Fact]
        public void Children_in_another_order_are_matched_by_name() {
            var scene = new Scene("Fungus1_29");
            var host = new GameObject("Moss", scene); var original = new GameObject("Moss", scene);
            host.Child("Hitbox", true); host.Child("Grass", false);
            var grass = original.Child("Grass", true); var hitbox = original.Child("Hitbox", false);
            Assert.Equal(0, BodyChildren.Apply(original, BodyChildren.Read(host)));
            Assert.True(hitbox.activeSelf); Assert.False(grass.activeSelf);
        }

        [Fact]
        public void Registered_children_are_neither_listed_nor_switched() {
            var scene = new Scene("Room");
            var host = new GameObject("Host", scene); var original = new GameObject("Original", scene);
            var listedManaged = host.Child("Enemy", true); listedManaged.Managed = true; host.Child("Hitbox", false);
            var managed = original.Child("Enemy", false); managed.Managed = true; var hitbox = original.Child("Hitbox", true);
            Assert.Equal(1, BodyChildren.Count(BodyChildren.Read(host)));
            Assert.Equal(0, BodyChildren.Apply(original, BodyChildren.Read(host)));
            Assert.False(managed.activeSelf); Assert.False(hitbox.activeSelf);
        }

        [Fact]
        public void A_child_registered_as_an_entity_of_its_own_is_not_an_own_child() {
            var scene = new Scene("Fungus1_04_boss"); var hornet = new GameObject("Hornet Boss 1", scene);
            var needle = hornet.Child("Needle", false); hornet.Child("Hitbox", true);
            RegisteredEntities.Add(needle);
            try {
                Assert.Equal(new[] { true }, BodyChildren.Read(hornet).ConvertAll(node => node.Active));
                Assert.Equal(0, BodyChildren.Apply(hornet, new System.Collections.Generic.List<BodyChildren.Node>()));
                Assert.False(needle.activeSelf);
            } finally {
                RegisteredEntities.Remove(needle);
            }
        }

        [Fact]
        public void Nested_children_and_duplicate_names_keep_each_individual_state() {
            var scene = new Scene("Room");
            var host = new GameObject("Host", scene); var original = new GameObject("Original", scene);
            host.Child("Arm", false).Child("Hitbox", true); host.Child("Arm", true);
            var first = original.Child("Arm", true); var nested = first.Child("Hitbox", false); var second = original.Child("Arm", false);
            Assert.Equal(0, BodyChildren.Apply(original, BodyChildren.Read(host)));
            Assert.False(first.activeSelf); Assert.True(nested.activeSelf); Assert.True(second.activeSelf);
        }

        [Fact]
        public void A_child_the_original_lacks_is_skipped_and_counted() {
            var scene = new Scene("Room");
            var host = new GameObject("Host", scene); var original = new GameObject("Original", scene);
            host.Child("Hitbox", false); host.Child("Spawned at runtime", true);
            var hitbox = original.Child("Hitbox", true);
            Assert.Equal(1, BodyChildren.Apply(original, BodyChildren.Read(host)));
            Assert.False(hitbox.activeSelf);
        }
    }
}
