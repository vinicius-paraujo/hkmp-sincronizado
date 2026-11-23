using System;
using Hkmp.Networking.Client.Connection;
using UnityEngine;

namespace Hkmp.Util;

/// <summary>
/// Drives the main thread mailbox from Unity's frame loop.
/// </summary>
internal class ThreadUtil : MonoBehaviour {
    /// <summary>
    /// Instantiate this static class.
    /// </summary>
    public static void Instantiate() {
        var threadUtilObject = new GameObject();
        threadUtilObject.AddComponent<ThreadUtil>();
        DontDestroyOnLoad(threadUtilObject);
    }

    /// <summary>
    /// Runs the given action on the main thread of Unity.
    /// </summary>
    /// <param name="action">The action to run.</param>
    public static void RunActionOnMainThread(Action action) {
        MainThreadMailbox.Shared.Post(action);
    }

    public void Update() {
        MainThreadMailbox.Shared.Drain();
    }
}
