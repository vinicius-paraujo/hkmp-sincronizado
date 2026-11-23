using System;
using System.Threading;

namespace Hkmp.Networking.Client.Connection;

/// <summary>
/// One handshake, run away from the main thread so the game does not stop while it waits. Only its result comes
/// back, posted to the mailbox; any exception is a failed result, so an attempt can never leave the connection
/// waiting. A cancelled attempt posts nothing, and what its handshake opened is closed by the attempt itself.
/// </summary>
internal sealed class ConnectAttempt
{
    private readonly object _lock = new();
    private bool _cancelled;

    private ConnectAttempt()
    {
    }

    /// <summary>
    /// Starts an attempt.
    /// </summary>
    /// <param name="handshake">Opens the connection; returns normally when the handshake ended well.</param>
    /// <param name="discard">Closes what <paramref name="handshake"/> opened, for an attempt cancelled meanwhile.</param>
    /// <param name="mailbox">Where the result goes.</param>
    /// <param name="succeeded">Runs on the main thread when the handshake ended well.</param>
    /// <param name="failed">Runs on the main thread with the exception when it did not.</param>
    /// <returns>The running attempt.</returns>
    public static ConnectAttempt Start(
        Action handshake,
        Action discard,
        MainThreadMailbox mailbox,
        Action succeeded,
        Action<Exception> failed
    )
    {
        var attempt = new ConnectAttempt();
        var thread = new Thread(() => attempt.Run(handshake, discard, mailbox, succeeded, failed))
        {
            IsBackground = true,
            Name = "HKMP connect attempt",
        };
        thread.Start();
        return attempt;
    }

    /// <summary>
    /// Cancels the attempt: its result will not be posted.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cancelled = true;
        }
    }

    private void Run(
        Action handshake,
        Action discard,
        MainThreadMailbox mailbox,
        Action succeeded,
        Action<Exception> failed
    )
    {
        Exception error = null;
        try
        {
            handshake();
        }
        catch (Exception exception)
        {
            error = exception;
        }

        bool cancelled;
        lock (_lock)
        {
            cancelled = _cancelled;
        }

        if (cancelled)
        {
            if (error == null)
            {
                Discard(discard);
            }

            return;
        }

        if (error == null)
        {
            mailbox.Post(() => DeliverSuccess(discard, succeeded));
        }
        else
        {
            mailbox.Post(() => DeliverFailure(error, failed));
        }
    }

    // A cancel can also come between the post and the drain; the result is dropped then too.
    private void DeliverSuccess(Action discard, Action succeeded)
    {
        if (IsCancelled())
        {
            Discard(discard);
            return;
        }

        succeeded();
    }

    private void DeliverFailure(Exception error, Action<Exception> failed)
    {
        if (!IsCancelled())
        {
            failed(error);
        }
    }

    private bool IsCancelled()
    {
        lock (_lock)
        {
            return _cancelled;
        }
    }

    private static void Discard(Action discard)
    {
        try
        {
            discard();
        }
        catch (Exception)
        {
            // Closing what a cancelled attempt opened can only fail on what is already closed.
        }
    }
}
