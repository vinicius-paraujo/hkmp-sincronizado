using System.Collections.Generic;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Reads and sets which own children of a body are switched on. Own children are the descendants that are not
/// entities of their own: registered children have their own handover, and the replica no longer has them.
/// </summary>
/// <remarks>
/// Children are matched by name and by occurrence among siblings of the same name, never by position: the order of
/// siblings can differ between copies of the game, for example once an object was reparented at runtime, and
/// switching on the wrong child can hide or break the body.
/// </remarks>
internal static class BodyChildren {
    /// <summary>One own child: its name, whether it is switched on, and its own children.</summary>
    internal sealed class Node {
        public string Name;
        public bool Active;
        public List<Node> Children = new List<Node>();
    }

    /// <summary>The own children of the body, as a tree.</summary>
    public static List<Node> Read(GameObject body) {
        var nodes = new List<Node>();
        foreach (Transform child in body.transform) {
            if (RegisteredEntities.IsEntity(child.gameObject)) {
                continue;
            }

            nodes.Add(new Node {
                Name = child.gameObject.name,
                Active = child.gameObject.activeSelf,
                Children = Read(child.gameObject),
            });
        }

        return nodes;
    }

    /// <summary>
    /// Switches the own children of the body on or off as listed. A listed child the body does not have is skipped,
    /// and a child of the body that is not listed is left as it is.
    /// </summary>
    /// <returns>How many listed children the body did not have.</returns>
    public static int Apply(GameObject body, List<Node> nodes) {
        var byName = new Dictionary<string, Queue<GameObject>>();
        foreach (Transform child in body.transform) {
            if (RegisteredEntities.IsEntity(child.gameObject)) {
                continue;
            }

            if (!byName.TryGetValue(child.gameObject.name, out var queue)) {
                queue = new Queue<GameObject>();
                byName[child.gameObject.name] = queue;
            }

            queue.Enqueue(child.gameObject);
        }

        var missing = 0;
        foreach (var node in nodes) {
            if (!byName.TryGetValue(node.Name, out var matches) || matches.Count == 0) {
                missing += 1 + Count(node.Children);
                continue;
            }

            var child = matches.Dequeue();
            child.SetActive(node.Active);
            missing += Apply(child, node.Children);
        }

        return missing;
    }

    /// <summary>How many children the tree lists, at every depth.</summary>
    public static int Count(List<Node> nodes) {
        var count = 0;
        foreach (var node in nodes) {
            count += 1 + Count(node.Children);
        }

        return count;
    }

    /// <summary>Whether two trees list the same children, switched on the same way.</summary>
    public static bool Same(List<Node> first, List<Node> second) {
        if (first.Count != second.Count) {
            return false;
        }

        for (var i = 0; i < first.Count; i++) {
            if (first[i].Name != second[i].Name || first[i].Active != second[i].Active
                || !Same(first[i].Children, second[i].Children)) {
                return false;
            }
        }

        return true;
    }
}
