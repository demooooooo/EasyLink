﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using VpnHood.Common.Collections;
using VpnHood.Common.Logging;
using VpnHood.Common.Utils;

namespace VpnHood.Tunneling;

internal class UdpProxyWorker : ITimeoutItem
{
    private readonly UdpProxyClient _udpProxyClient;
    private readonly UdpClient _udpClient;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    public TimeoutDictionary<IPEndPoint, TimeoutItem<IPEndPoint>> DestinationEndPointMap { get; }
    public DateTime AccessedTime { get; set; }
    public AddressFamily AddressFamily { get; }
    public bool IsDisposed { get; private set; }

    public UdpProxyWorker(UdpProxyClient udpProxyClient, UdpClient udpClient, AddressFamily addressFamily)
    {
        _udpProxyClient = udpProxyClient;
        _udpClient = udpClient;
        AddressFamily = addressFamily;
        DestinationEndPointMap = new TimeoutDictionary<IPEndPoint, TimeoutItem<IPEndPoint>>(udpProxyClient.Timeout);
        AccessedTime = FastDateTime.Now;

        // prevent raise exception when there is no listener
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            udpClient.Client.IOControl(-1744830452, new byte[] { 0 }, new byte[] { 0 });
        }

        _ = Listen();
    }

    private bool IsInvalidState(Exception ex)
    {
        return IsDisposed || ex is ObjectDisposedException
            or SocketException { SocketErrorCode: SocketError.InvalidArgument };
    }

    public async ValueTask SendPacket(IPEndPoint ipEndPoint, byte[] datagram, bool? noFragment)
    {
        AccessedTime = FastDateTime.Now;

        try
        {
            await _sendSemaphore.WaitAsync();

            if (VhLogger.IsDiagnoseMode)
                VhLogger.Instance.Log(LogLevel.Information, GeneralEventId.Udp,
                    $"Sending all udp bytes to host. Requested: {datagram.Length}, From: {VhLogger.Format(_udpClient.Client.LocalEndPoint)}, To: {VhLogger.Format(ipEndPoint)}");

            // IpV4 fragmentation
            if (noFragment != null)
                _udpClient.DontFragment = noFragment.Value; // Never call this for IPv6, it will throw exception for any value

            var sentBytes = await _udpClient.SendAsync(datagram, datagram.Length, ipEndPoint);
            if (sentBytes != datagram.Length)
                VhLogger.Instance.LogWarning(
                    $"Couldn't send all udp bytes. Requested: {datagram.Length}, Sent: {sentBytes}");
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogWarning(
                $"Couldn't send a udp packet to {VhLogger.Format(ipEndPoint)}. Error: {ex.Message}");

            if (IsInvalidState(ex))
                Dispose();
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    public async Task Listen()
    {
        while (!IsDisposed)
        {
            var udpResult = await _udpClient.ReceiveAsync();
            AccessedTime = FastDateTime.Now;

            // find the audience
            if (!DestinationEndPointMap.TryGetValue(udpResult.RemoteEndPoint, out var sourceEndPoint))
            {
                VhLogger.Instance.LogInformation(GeneralEventId.Udp, "Could not find result UDP in the NAT!");
                return;
            }

            // create packet for audience
            var ipPacket = PacketUtil.CreateIpPacket(udpResult.RemoteEndPoint.Address, sourceEndPoint.Value.Address);
            var udpPacket = new UdpPacket((ushort)udpResult.RemoteEndPoint.Port, (ushort)sourceEndPoint.Value.Port)
            {
                PayloadData = udpResult.Buffer
            };

            ipPacket.PayloadPacket = udpPacket;
            PacketUtil.UpdateIpPacket(ipPacket);

            // send packet to audience
            await _udpProxyClient.OnPacketReceived(ipPacket);
        }
    }

    public void Dispose()
    {
        IsDisposed = true;
        _udpClient.Dispose();
    }
}