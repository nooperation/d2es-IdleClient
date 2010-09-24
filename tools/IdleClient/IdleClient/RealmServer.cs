﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.IO;

namespace IdleClient.Realm
{
	/// <summary>Arguments for game server. </summary>
	public class GameServerArgs : EventArgs
	{
		/// <summary>Gets or sets the game server address.</summary>
		public string Address { get; set; }

		/// <summary>Gets or sets the game server port.</summary>
		public int Port { get; set; }

		/// <summary>Gets or sets the game hash.</summary>
		public uint GameHash { get; set; }

		/// <summary>Gets or sets the game token.</summary>
		public ushort GameToken { get; set; }

		/// <summary>Gets or sets the character class.</summary>
		public byte CharacterClass { get; set; }

		/// <summary>Gets or sets the name of the character. Must be padded to 16 bytes.</summary>
		public byte[] CharacterName { get; set; }
	}

	/// <summary>
	/// Realm server connection manager.  
	/// </summary>
	class RealmServer
	{
		/// <summary> Event queue for all listeners interested in OnDisconnect events. </summary>
		public event EventHandler OnDisconnect;

		/// <summary> 
		/// Event queue for all listeners interested in ReadyToConnectToGameServer events. There
		/// must exist atleast one listener to this event. 
		/// </summary>
		public event EventHandler<GameServerArgs> ReadyToConnectToGameServer;

		private bool isDisconnecting;
		private TcpClient client = new TcpClient();
		private Config settings;

		/// <summary>
		/// Constructor. 
		/// </summary>
		/// <param name="settings">Options for controlling the operation.</param>
		public RealmServer(Config settings)
		{
			this.settings = settings;
		}

		/// <summary>
		/// Entry point for realm server thread. 
		/// </summary>
		/// <param name="args">The realm server arguments from the chat server.</param>
		public void Run(object args)
		{
			Chat.RealmServerArgs realmServerData = args as Chat.RealmServerArgs;
			string address = realmServerData.Ip.ToString();
			int port = realmServerData.Port;

			Console.WriteLine("Connected to realm server " + address + ":" + port);

			try
			{
				client = new TcpClient(address, port);
			}
			catch (SocketException ex)
			{
				Console.WriteLine("Failed to connect to realm server: " + ex.Message);
				FireOnDisconnectEvent();
				return;
			}

			using (client)
			{
				// Used to store the unprocessed packet data from ReceivePacket
				byte[] buffer = new byte[0];

				// When connecting to the realm server we must specify the protocol to use.
				NetworkStream ns = client.GetStream();
				ns.WriteByte(0x01);

				SendPacket(new StartupOut(realmServerData));

				while (client.Connected)
				{
					RealmServerPacket packet;

					try
					{
						packet = ReceivePacket(ref buffer);
					}
					catch (Exception ex)
					{
						if (!isDisconnecting)
						{
							Console.WriteLine("Failed to receive realm server packet: " + ex.Message);
							Disconnect();
						}
						break;
					}

					if (settings.ShowPackets)
					{
						Console.WriteLine("S -> C: " + packet);
					}
					if (settings.ShowPacketData)
					{
						Console.WriteLine("Data: {0:X2} {1}", (byte)packet.Id, Util.GetStringOfBytes(packet.Data, 0, packet.Data.Length));
					}

					switch (packet.Id)
					{
						case RealmServerPacketType.STARTUP:
							OnStartup(packet);
							break;
						case RealmServerPacketType.CREATEGAME:
							OnCreateGame(packet);
							break;
						case RealmServerPacketType.JOINGAME:
							OnJoinGame(packet);
							break;
						case RealmServerPacketType.GAMEINFO:
							OnGameInfo(packet);
							break;
						case RealmServerPacketType.CHARLOGON:
							OnCharLogon(packet);
							break;
						case RealmServerPacketType.CREATEQUEUE:
							OnCreateQueue(packet);
							break;
						case RealmServerPacketType.CHARLIST2:
							OnCharList2(packet);
							break;
						default:
							break;
					}
				}
			}

			Console.WriteLine("Realm server: Disconnected");
			FireOnDisconnectEvent();
		}

		/// <summary>
		/// Handles the JoinGameIn packet. This packet is sent in response to our request to join
		/// a game. If successful, we fire an event to notify the driver to start the game server.
		/// </summary>
		/// <param name="packet">The packet.</param>
		private void OnJoinGame(RealmServerPacket packet)
		{
			JoinGameIn fromServer = new JoinGameIn(packet);
			Console.WriteLine(fromServer);

			if (!fromServer.IsSuccessful())
			{
				Disconnect();
				return;
			}

			GameServerArgs args = new GameServerArgs();
			args.Address = fromServer.GameServerIp.ToString();
			args.Port = 4000;
			args.GameHash = fromServer.GameHash;
			args.GameToken = fromServer.GameToken;
			args.CharacterClass = (byte)CharacterClassType.Barbarian;

			// Pad the character name to 16 bytes. This is required by the game server.
			byte[] charNameBytes = ASCIIEncoding.ASCII.GetBytes(settings.CharacterName);
			Array.Resize(ref charNameBytes, 16);
			args.CharacterName = (byte[])charNameBytes.Clone();

			ReadyToConnectToGameServer(this, args);
		}

		/// <summary>
		/// Handles the GameInfoIn packet.
		/// </summary>
		/// <param name="packet">The packet.</param>
		private void OnGameInfo(RealmServerPacket packet)
		{
			GameInfoIn fromServer = new GameInfoIn(packet);
			Console.WriteLine(fromServer);

			JoinGameOut toServer = new JoinGameOut(settings.GameName, settings.GamePass);
			SendPacket(RealmServerPacketType.JOINGAME, toServer.GetBytes());
		}

		/// <summary>
		/// Handles the CreateGameIn packet. This packet is sent in response to our CreateGameOut
		/// request. If successful, we will request the game info.
		/// </summary>
		/// <param name="packet">The packet.</param>
		private void OnCreateGame(RealmServerPacket packet)
		{
			CreateGameIn fromServer = new CreateGameIn(packet);
			Console.WriteLine(fromServer);

			if (!fromServer.IsSuccessful())
			{
				Disconnect();
				return;
			}

			GameInfoOut toServer = new GameInfoOut(settings.GameName);
			SendPacket(RealmServerPacketType.GAMEINFO, toServer.GetBytes());
		}

		/// <summary>
		/// Handles the CreateQueueIn packet. This packet is sent when you're forced to enter a
		/// join game queue. CreateQueueIn will be resent every now and then to update your position
		/// until your request is finally accepted, which will send us a JoinGameIn packet.
		/// </summary>
		/// <param name="packet">The packet.</param>
		private void OnCreateQueue(RealmServerPacket packet)
		{
			CreateQueueIn fromServer = new CreateQueueIn(packet);
			Console.WriteLine(fromServer);
		}

		/// <summary>
		/// Handles the CharLogonIn packet. This packet is sent in response to a CharLogonOut
		/// packet. If this packet status is successful, we respond by requesting to create a game.
		/// </summary>
		/// <remarks>
		/// Requesting to create a game that already exists is correct because we're told if the
		/// game already exists or has been created successfully when the server responds with a
		/// CreateGameIn packet. From there we just query and join it.
		/// </remarks>
		/// <param name="packet">The packet.</param>
		private void OnCharLogon(RealmServerPacket packet)
		{
			CharLogonIn fromServer = new CharLogonIn(packet);
			Console.WriteLine(fromServer);

			if (!fromServer.IsSuccessful())
			{
				Disconnect();
				return;
			}

			CreateGameOut toServer = new CreateGameOut(settings.GameName, settings.GamePass, settings.GameDescription, settings.GameDifficulty, 1);
			SendPacket(RealmServerPacketType.CREATEGAME, toServer.GetBytes());
		}

		/// <summary>
		/// Handles the CharList2In packet. This packet contains a list of all the characters on the
		/// account. We respond by sending a CharLogonOut packet containing the name of the character
		/// to logon as.
		/// </summary>
		/// <param name="packet">The packet.</param>
		private void OnCharList2(RealmServerPacket packet)
		{
			CharList2In fromServer = new CharList2In(packet);
			Console.WriteLine(fromServer);

			if (!fromServer.CharacterExists(settings.CharacterName))
			{
				Console.WriteLine("Realm server: Character not found");
				Disconnect();
				return;
			}

			CharLogonOut toServer = new CharLogonOut(settings.CharacterName);
			SendPacket(RealmServerPacketType.CHARLOGON, toServer.GetBytes());
		}

		/// <summary>
		/// Handles the StartupIn packet. This packet is sent in response to our StartupOut packet when
		/// we initially connected to the Realm Server. If successful, we request a list of characters
		/// on the account.
		/// </summary>
		/// <param name="packet">The packet.</param>
		private void OnStartup(RealmServerPacket packet)
		{
			StartupIn fromServer = new StartupIn(packet);
			Console.WriteLine(fromServer);

			if (!fromServer.IsSuccessful())
			{
				Console.WriteLine("Realm server: server denied our connection request");
				Disconnect();
			}

			CharList2Out toServer = new CharList2Out(8);
			SendPacket(RealmServerPacketType.CHARLIST2, toServer.GetBytes());
		}

		/// <summary>
		/// Sends a packet to the realm server. 
		/// </summary>
		/// <param name="packet">The packet.</param>
		public void SendPacket(IOutPacket packet)
		{
			SendPacket((RealmServerPacketType)packet.Id, packet.GetBytes());
		}

		/// <summary>
		/// Sends a packet to the realm server.
		/// </summary>
		/// <param name="type">The type of packet to send.</param>
		/// <param name="data">The packet data (Not the packet header).</param>
		public void SendPacket(RealmServerPacketType type, byte[] data)
		{
			RealmServerPacket packet;

			// Header length is 4 bytes, so total packet length is 4 + data length
			packet.Length = (ushort)(data.Length + 4);
			packet.Id = type;
			packet.Data = data;

			if (settings.ShowPackets)
			{
				Console.WriteLine("C -> S: " + packet);
			}
			if (settings.ShowPacketData)
			{
				Console.WriteLine("Data: {0:X2} {1}", (byte)packet.Id, Util.GetStringOfBytes(packet.Data, 0, packet.Data.Length));
			}

			byte[] packetBytes = packet.GetBytes();
			try
			{
				client.GetStream().Write(packetBytes, 0, packetBytes.Length);
			}
			catch (Exception ex)
			{
				if (!isDisconnecting)
				{
					Console.WriteLine("Failed to send packet to realm server: " + ex.Message);
					Disconnect();
				}
				return;
			}
		}

		/// <summary>
		/// Receives a realm server packet. 
		/// </summary>
		/// <param name="buffer">
		/// [in,out] The buffer used to hold excess data retrieved from the server for use in future
		/// calls to ReceivePacket.
		/// </param>
		/// <returns>Realm server packet </returns>
		private RealmServerPacket ReceivePacket(ref byte[] buffer)
		{
			RealmServerPacket packet;
			bool needsMoreData = false;

			while (true)
			{
				if (buffer.Length == 0 || needsMoreData)
				{
					Util.Receive(client.GetStream(), ref buffer);
					needsMoreData = false;
				}

				// Needs enough data for header portion
				if (buffer.Length < 3)
				{
					needsMoreData = true;
					continue;
				}

				// Create the packet header since we've read enough bytes for it to exist
				BinaryReader br = new BinaryReader(new MemoryStream(buffer));
				packet.Length = br.ReadUInt16();
				packet.Id = (RealmServerPacketType)br.ReadByte();

				// We need more data for the packet
				if (packet.Length > buffer.Length)
				{
					needsMoreData = true;
					continue;
				}

				packet.Data = br.ReadBytes(packet.Length - 3);
				break;
			}

			// Remove the processed portion of the packet data from the buffer
			int newBufferLength = buffer.Length - packet.Length;
			Array.Copy(buffer, packet.Length, buffer, 0, newBufferLength);
			Array.Resize(ref buffer, newBufferLength);

			return packet;
		}

		/// <summary>
		/// Forcefully disconnects from the realm server.
		/// </summary>
		public void Disconnect()
		{
			if (client.Connected)
			{
				Console.WriteLine("Realm server: Disconnect requested");
				isDisconnecting = true;
				client.Close();
			}
		}

		/// <summary>
		/// Raises the on disconnect event.
		/// </summary>
		private void FireOnDisconnectEvent()
		{
			EventHandler tempHandler = OnDisconnect;
			if (tempHandler != null)
			{
				tempHandler(this, new EventArgs());
			}
		}
	}
}