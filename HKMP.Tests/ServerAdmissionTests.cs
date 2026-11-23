using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Linq.Expressions;
using Xunit;

namespace Hkmp.Tests {
    public class ServerAdmissionTests {
        [Fact]
        public void Real_server_does_not_finish_before_queuing_the_login_response() {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../HKMP/bin/Release/net472/HKMP.dll"));
            var assembly = Assembly.LoadFrom(path);
            var senderType = assembly.GetType("Hkmp.Networking.Chunk.ServerChunkSender", true);
            var sender = Activator.CreateInstance(senderType, new object[] { null });
            var receiver = Activator.CreateInstance(assembly.GetType("Hkmp.Networking.Chunk.ServerChunkReceiver", true), new object[] { null });
            var type = assembly.GetType("Hkmp.Networking.Server.ServerConnectionManager", true);
            // The manager reads the client's ID through a function, since a connection that takes over a session
            // changes its ID, and it takes the factory of its timers and the admission deadline; the thread-pool
            // timers need no server core.
            var timers = assembly.GetType("Hkmp.Networking.Timing.SystemUpdateTimerFactory", true)
                .GetField("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var manager = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { null, sender, receiver, (Func<ushort>) (() => 2), timers, TimeSpan.FromSeconds(20) }, null);
            var completed = false;
            var request = type.GetEvent("ConnectionRequestEvent");
            var parameters = request.EventHandlerType.GetMethod("Invoke").GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            Action<object> reject = response => {
                var result = response.GetType().GetProperty("ConnectionResult");
                result.SetValue(response, Enum.Parse(result.PropertyType, "RejectedOther"));
                response.GetType().GetProperty("ConnectionRejectedMessage").SetValue(response, "Username already in use");
                type.GetMethod("FinishConnection").Invoke(manager, new object[] { (Action) (() => completed = true) });
            };
            request.AddEventHandler(manager, Expression.Lambda(request.EventHandlerType,
                Expression.Invoke(Expression.Constant(reject), Expression.Convert(parameters[2], typeof(object))), parameters).Compile());
            var info = Activator.CreateInstance(assembly.GetType("Hkmp.Networking.Packet.Data.ClientInfo", true));
            type.GetMethod("ProcessClientInfo").Invoke(manager, new[] { info });
            Assert.False(completed);
            var queue = senderType.BaseType.GetField("_toSendPackets", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sender);
            Assert.Equal(1, queue.GetType().GetProperty("Count").GetValue(queue));
            var pending = queue.GetType().GetMethod("Take", Type.EmptyTypes).Invoke(queue, null);
            ((Action) pending.GetType().GetField("Item2").GetValue(pending))();
            Assert.True(completed);
        }
    }
}
