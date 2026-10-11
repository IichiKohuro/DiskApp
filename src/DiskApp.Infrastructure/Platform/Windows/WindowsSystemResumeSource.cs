using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Пробуждение Windows через <c>PowerRegisterSuspendResumeNotification</c> с обратным вызовом: окно не нужно,
/// уведомление приходит и при пробуждении без пользователя (<c>PBT_APMRESUMEAUTOMATIC</c>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemResumeSource : ISystemResumeSource
{
    /// <inheritdoc />
    public IDisposable Subscribe(Action onResumed)
    {
        ArgumentNullException.ThrowIfNull(onResumed);
        return new Subscription(onResumed);
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    private sealed unsafe class Subscription : IDisposable
    {
        private const uint DeviceNotifyCallback = 2;
        private const uint PbtApmResumeAutomatic = 0x12;

        private readonly Action _onResumed;
        private GCHandle _self;
        private IntPtr _parameters;
        private IntPtr _registration;

        public Subscription(Action onResumed)
        {
            _onResumed = onResumed;
            _self = GCHandle.Alloc(this);

            // Структура DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS живёт в неуправляемой памяти до отмены подписки.
            _parameters = Marshal.AllocHGlobal(sizeof(SubscribeParameters));
            *(SubscribeParameters*)_parameters = new SubscribeParameters
            {
                Callback = &OnPowerEvent,
                Context = GCHandle.ToIntPtr(_self),
            };

            if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration) != 0)
            {
                // Без уведомлений приложение работает как раньше: офлайн-корни находит обычный опрос.
                Release();
            }
        }

        public void Dispose()
        {
            if (_registration != IntPtr.Zero)
            {
                _ = PowerUnregisterSuspendResumeNotification(_registration);
                _registration = IntPtr.Zero;
            }

            Release();
        }

        [UnmanagedCallersOnly]
        private static uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
        {
            if (type == PbtApmResumeAutomatic && GCHandle.FromIntPtr(context).Target is Subscription subscription)
            {
                // Системный поток ждёт возврата: работа уходит в пул.
                ThreadPool.UnsafeQueueUserWorkItem(static action => action(), subscription._onResumed, preferLocal: false);
            }

            return 0;
        }

        private void Release()
        {
            if (_parameters != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_parameters);
                _parameters = IntPtr.Zero;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SubscribeParameters
        {
            public delegate* unmanaged<IntPtr, uint, IntPtr, uint> Callback;
            public IntPtr Context;
        }
    }
}
