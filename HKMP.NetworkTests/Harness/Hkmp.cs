using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// Access to the built HKMP assembly. Newer checks use its internal types directly; the transport harness still
/// reaches private members by reflection.
/// </summary>
internal static class Hkmp {
    public static readonly Assembly Assembly = typeof(global::Hkmp.Networking.Packet.PacketManager).Assembly;

    public static Type Type(string name) => Assembly.GetType(name, true);

    /// <summary>
    /// Invokes a method and rethrows the exception the method itself threw, not the reflection wrapper.
    /// </summary>
    public static object Call(object target, string method, params object[] arguments) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var types = Array.ConvertAll(arguments, argument => argument.GetType());
        var info = target.GetType().GetMethod(method, flags, null, types, null) ?? target.GetType().GetMethod(method, flags);
        try {
            return info.Invoke(target, arguments);
        } catch (TargetInvocationException e) {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    public static object Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);

    /// <summary>
    /// A UDP port that is free on the loopback interface right now.
    /// </summary>
    public static int FreeUdpPort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint) socket.LocalEndPoint).Port;
    }
}
