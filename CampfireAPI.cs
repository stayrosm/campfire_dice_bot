using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Xml;
using System.Xml.Linq;

namespace Campfire
{
    public class UnexpectedResponseException : ApplicationException
    {
    }

    internal static class XElementExtensions
    {
        public static string ElementValue(this XElement node, XName name)
        {
            XElement child = node.Element(name);
            if (child == null)
            {
                return null;
            }
            return child.Value.Trim();
        }

        public static string ElementValueOrDefault(this XElement node, XName name, string defaultValue)
        {
            string value = node.ElementValue(name);
            if (value == null)
            {
                return defaultValue;
            }
            return value;
        }

        public static string ElementValueOrThrow(this XElement node, XName name)
        {
            string value = node.ElementValue(name);
            if (value == null)
            {
                throw new UnexpectedResponseException();
            }
            return value;            
        }
    }

    public class Server
    {
        private string m_Domain;
        private string m_AuthToken;

        private XmlReaderSettings m_ReaderSettings;

        public Server(string domain, string authToken)
        {
            m_Domain = domain;
            m_AuthToken = authToken;

            m_ReaderSettings = new XmlReaderSettings();
            m_ReaderSettings.ConformanceLevel = ConformanceLevel.Fragment;
            m_ReaderSettings.XmlResolver = null;
        }

        public Server(string domain, string username, string password)
            : this(domain, username + ":" + password)
        {
            User authUser = GetAuthenticatedUser();
            if (authUser.AuthToken == null)
            {
                throw new UnexpectedResponseException();
            }
            m_AuthToken = authUser.AuthToken;
        }

        public User GetUserById(int id)
        {
            return new User(this, id);
        }

        public User GetAuthenticatedUser()
        {
            return new User(this);
        }

        public Room GetRoomById(int id)
        {
            return new Room(this, id);
        }

        public RoomCollection GetAllRooms()
        {
            return new RoomCollection(this, "rooms");
        }

        public RoomCollection GetPresentRooms()
        {
            return new RoomCollection(this, "presence");
        }

        #region HTTP Helper Functions

        // MakeApiCall() is the core function used to actually talk to the
        // Campfire server; no network transfer happens except during its
        // runtime.

        internal delegate void ResultParser(Server server, XmlReader reader);

        internal void MakeApiCall(string method, 
                                  string apiCall,
                                  byte[] postData,                                  
                                  ResultParser resultParser)
        {
            bool useStreaming = apiCall.EndsWith("live.xml");
            string host = useStreaming ? "streaming.campfirenow.com" : m_Domain;
            UriBuilder uri = new UriBuilder("https", host, -1, apiCall);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri.Uri);
            request.Accept = "text/javascript, text/html, application/xml, text/xml, */*";
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(m_AuthToken));
            request.Method = method;
            if (postData != null)
            {
                request.ContentType = "application/xml";
                using (Stream postDataStream = request.GetRequestStream())
                {
                    postDataStream.Write(postData, 0, postData.Length);
                    postDataStream.Close();
                }
            }

            using (WebResponse response = request.GetResponse())
            {
                using (Stream responseStream = response.GetResponseStream())
                {
                    using (XmlReader reader = XmlReader.Create(responseStream, m_ReaderSettings))
                    {
                        resultParser(this, reader);

                        if (useStreaming)
                        {
                            request.Abort();
                        }
                        reader.Close();
                    }
                    responseStream.Close();
                }
                response.Close();
            }
        }

        internal static void ParseEmptyXML(Server server, XmlReader reader)
        {
            while (reader.Read())
            {
                if ((reader.NodeType != XmlNodeType.Whitespace) && (reader.NodeType != XmlNodeType.SignificantWhitespace))
                {
                    throw new UnexpectedResponseException();
                }
            }
        }

        #endregion
    }

    public class User
    {
        public int ID { get; private set; }
        public string Name { get; private set; }
        public string Email { get; private set; }
        public bool IsAdmin { get; private set; }
        public bool IsGuest { get; private set; }
        public DateTime CreatedAt { get; private set; }

        public string AuthToken { get; private set; }       // Not always available

        internal User(Server server)
        {
            // Get the authenticated user used to log into the server.  This
            // is the only API call that will accept a username/password pair
            // instead of an authentication token, and if used, the <user>
            // response will contain an extra <api-auth-token> element.
            // If the Server object was created using the username/password
            // constructor, it will call this to convert it to an auth token
            // and use the auth token for all future calls.

            server.MakeApiCall("GET", 
                               "users/me.xml",
                               null,                               
                               ParseResponse);
        }

        internal User(Server server, int userID)
        {
            // Get a user by ID.  (This will never contain api-auth-token,
            // even if the requested user is the caller.)

            server.MakeApiCall("GET", 
                               "users/" + userID.ToString() + ".xml",
                               null,                               
                               ParseResponse);
        }

        internal User(Server server, XElement userNode)
        {
            // Build from a <user> token.  If a room is queried by ID, we
            // will get the current user list embedded inside it.

            InterpretXML(userNode);
        }

        #region XML Parsing

        private void ParseResponse(Server server, XmlReader reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name.Equals("user"))
                    {
                        InterpretXML((XElement)XElement.Load(reader));
                        return;
                    }
                }
            }
            throw new UnexpectedResponseException();
        }

        private void InterpretXML(XElement userNode)
        {
            ID = Convert.ToInt32(userNode.ElementValueOrThrow("id"));
            Name = userNode.ElementValueOrThrow("name");
            Email = userNode.ElementValueOrThrow("email-address");
            IsAdmin = userNode.ElementValueOrThrow("admin").Equals("true");
            IsGuest = userNode.ElementValueOrThrow("type").Equals("Guest");
            try
            {
                CreatedAt = DateTime.Parse(userNode.ElementValueOrThrow("created-at"));
            }
            catch (FormatException)
            {
                throw new UnexpectedResponseException();
            }

            // The api-auth-token parameter is only present if we
            // are called using me.xml - it will have null otherwise.

            AuthToken = userNode.ElementValue("api-auth-token");
        }

        #endregion
    }

    public class Message
    {
        private Server m_Server = null;

        public int ID { get; private set; }

        public int RoomID { get; private set; }
        public Room Room
        {
            get
            {
                return m_Server.GetRoomById(RoomID);
            }
        }

        // UserID will be null for certain message types, most notably
        // TimestampMessage.
        
        public int? UserID { get; private set; }
        public User User
        {
            get
            {
                if (UserID.HasValue)
                {
                    return m_Server.GetUserById(UserID.Value);
                }
                return null;
            }
        }
        
        public string Body { get; private set; }
        public string Type { get; private set; }
        public DateTime CreatedAt { get; private set; }

        private Message()
        {
            // Only used by Message.Send(); fields will be initialized
            // by InitFromNewMessage().
        }

        internal Message(Server server, XElement messageNode)
        {
            // Used to read messages returned from Room.Search()/Recent()
            // or the result of a Message.SendMessage() call.

            InterpretXML(server, messageNode);
        }

        static internal Message Send(Server server, int roomID, string type, string body)
        {
            Message msg = new Message();
            msg.InitFromNewMessage(server, roomID, type, body);
            return msg;
        }

        private void InitFromNewMessage(Server server, int roomID, string type, string body)
        {
            // Sends a new message to the server.  Used by Room.Speak();

            StringBuilder messageBuilder = new StringBuilder("<message>");
            messageBuilder.AppendFormat("<type>{0}</type>", type);
            messageBuilder.AppendFormat("<body>{0}</body>", body);
            messageBuilder.Append("</message>");

            server.MakeApiCall("POST",
                               "room/" + roomID + "/speak.xml",
                               new UTF8Encoding().GetBytes(messageBuilder.ToString()),
                               ParseResponse);
        }

        #region XML Parsing

        private void ParseResponse(Server server, XmlReader reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name.Equals("message"))
                    {
                        InterpretXML(server, XElement.Load(reader));
                        return;
                    }
                }
            }
            throw new UnexpectedResponseException();
        }

        private void InterpretXML(Server server, XElement roomNode)
        {
            m_Server = server;

            ID = Convert.ToInt32(roomNode.ElementValueOrThrow("id"));
            RoomID = Convert.ToInt32(roomNode.ElementValueOrThrow("room-id"));
            
            string userIdString = roomNode.ElementValue("user-id");
            if (userIdString.Length == 0)
            {
                UserID = null;
            }
            else
            {
                UserID = Convert.ToInt32(userIdString);
            }

            Body = roomNode.ElementValueOrDefault("body", "");
            Type = roomNode.ElementValueOrThrow("type");

            try
            {
                CreatedAt = DateTime.Parse(roomNode.ElementValueOrThrow("created-at"));
            }
            catch (FormatException)
            {
                throw new UnexpectedResponseException();
            }
        }

        #endregion
    }

    public class RoomCollection : IEnumerable<Room>
    {
        private List<Room> m_Rooms = new List<Room>();

        internal RoomCollection(Server server, string callType)
        {
            server.MakeApiCall("GET",
                               callType + ".xml",
                               null,
                               ParseResponse);
        }

        #region XML Parsing

        private void ParseResponse(Server server, XmlReader reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name.Equals("rooms"))
                    {
                        InterpretXML(server, XElement.Load(reader));
                        return;
                    }
                }
            }
            throw new UnexpectedResponseException();
        }

        private void InterpretXML(Server server, XElement roomArrayNode)
        {
            foreach (XElement roomNode in roomArrayNode.Elements("room"))
            {
                m_Rooms.Add(new Room(server, roomNode));
            }
        }

        #endregion

        public IEnumerator<Room> GetEnumerator()
        {
            return m_Rooms.GetEnumerator();
        }

        IEnumerator<Room> IEnumerable<Room>.GetEnumerator()
        {
            return ((IEnumerable<Room>)m_Rooms).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return ((System.Collections.IEnumerable)m_Rooms).GetEnumerator();
        }        
    }

    public class Room
    {
        private Server m_Server = null;

        public int ID { get; private set; }
        public string Name { get; private set; }
        public string Topic { get; private set; }
        public int MemberLimit { get; private set; }
        public bool IsLocked { get; private set; }
        public DateTime UpdatedAt { get; private set; }
        public DateTime CreatedAt { get; private set; }

        // Not always available.

        public bool? IsFull { get; private set; }
        public bool? IsOpenToGuests { get; private set; }
        public string GuestToken { get; private set; }

        private List<User> m_Users = null;
        public IEnumerable<User> Users
        {
            get
            {
                if (m_Users == null)
                {
                    return null;
                }
                return m_Users.AsEnumerable();
            }
        }

        internal Room(Server server, int roomID)
        {
            m_Server = server;
            ID = roomID;
            Refresh();
        }

        internal Room(Server server, XElement roomNode)
        {
            InterpretXML(server, roomNode);
        }

        #region XML Parsing

        private void ParseResponse(Server server, XmlReader reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name.Equals("room"))
                    {
                        InterpretXML(server, XElement.Load(reader));
                        return;
                    }
                }
            }
            throw new UnexpectedResponseException();
        }

        private void InterpretXML(Server server, XElement roomNode)
        {
            m_Server = server;

            ID = Convert.ToInt32(roomNode.ElementValueOrThrow("id"));
            Name = roomNode.ElementValueOrThrow("name");
            Topic = roomNode.ElementValueOrThrow("topic");
            MemberLimit = Convert.ToInt32(roomNode.ElementValueOrThrow("membership-limit"));
            IsLocked = roomNode.ElementValueOrThrow("locked").Equals("true");
            try
            {
                UpdatedAt = DateTime.Parse(roomNode.ElementValueOrThrow("updated-at"));
                CreatedAt = DateTime.Parse(roomNode.ElementValueOrThrow("created-at"));
            }
            catch (FormatException)
            {
                throw new UnexpectedResponseException();
            }          

            // The following values are only available when called from
            // the room/#.xml API, not rooms.xml.  Since some managed
            // languages don't support nullable types yet, we resort to
            // storing these as strings - gross.

            string full = roomNode.ElementValue("full");
            if (full == null)
            {
                IsFull = null;
            }
            else
            {
                IsFull = full.Equals("true");
            }

            string openToGuests = roomNode.ElementValue("open-to-guests");
            if (openToGuests == null)
            {
                IsOpenToGuests = null;
            }
            else
            {
                IsOpenToGuests = openToGuests.Equals("true");
            }

            GuestToken = roomNode.ElementValue("active-token-value");

            XElement userArrayNode = roomNode.Element("users");
            if (userArrayNode != null)
            {
                m_Users = new List<User>();
                foreach (XElement userNode in userArrayNode.Elements("user"))
                {
                    m_Users.Add(new User(server, userNode));
                }
            }
        }

        #endregion

        public void Refresh()
        {
            m_Server.MakeApiCall("GET",
                                 "room/" + ID.ToString() + ".xml",
                                 null,
                                 ParseResponse);
        }

        public void Join()
        {
            m_Server.MakeApiCall("POST",
                                 "room/" + ID + "/join.xml",
                                 new byte[] {},
                                 Server.ParseEmptyXML); 
        }

        public void Leave()
        {
            m_Server.MakeApiCall("POST",
                                 "room/" + ID + "/leave.xml",
                                 new byte[] {},
                                 Server.ParseEmptyXML);
        }

        public void Lock()
        {
            // NOTE: As of Sep-2010, it was possible for an API user to lock
            // a room without being inside it, rendering it un-unlockable.
            // It might be worthwhile to make a Refresh() call and check that
            // the user is currently inside the room before locking it.

            m_Server.MakeApiCall("POST",
                                 "room/" + ID + "/lock.xml",
                                 new byte[] {},
                                 Server.ParseEmptyXML);
        }

        public void Unlock()
        {
            m_Server.MakeApiCall("POST",
                                 "room/" + ID + "/unlock.xml",
                                 new byte[] {},
                                 Server.ParseEmptyXML);
        }

        public void Update(string name, string topic)
        {
            // NOTE: As of Sep-2010, it was possible for an API user to change
            // the room's topic without being inside it.  It might be worthwhile
            // to make a Refresh() call and check that the user is currently
            // inside the room before allowing them to take this action.

            if ((name == null) && (topic == null))
            {
                throw new ArgumentException();
            }

            StringBuilder roomUpdateRequest = new StringBuilder("<room>");
            if (name != null)
            {
                // TODO: Escape name as necessary
                roomUpdateRequest.AppendFormat("<name>{0}</name>", name);
            }
            if (topic != null)
            {
                // TODO: Escape topic as necessary
                roomUpdateRequest.AppendFormat("<topic>{0}</topic>", topic);
            }
            roomUpdateRequest.Append("</room>");
            
            m_Server.MakeApiCall("PUT",
                                 "room/" + ID + ".xml",
                                 new UTF8Encoding().GetBytes(roomUpdateRequest.ToString()),
                                 Server.ParseEmptyXML);
        }

        public Message Speak(string type, string body)
        {
            return Message.Send(m_Server, ID, type, body);
        }

        public delegate bool OnMessage(Room room, Message message);

        private class RoomListener
        {
            private Room m_Room;
            private OnMessage m_Callback;

            internal RoomListener(Room room, OnMessage callback)
            {
                m_Room = room;
                m_Callback = callback;
            }

            internal void ParseResponse(Server server, XmlReader reader)
            {            
                // One of the major problems with the XmlReader and XElement
                // model is that they don't deal well with streaming content.
                // Given a stream of <message> tags, using reader.ReadOuterXml()
                // or XElement.Load/()ReadFrom(), you won't return until you
                // get a followup tag.  Both of them are looking for the next
                // XNode to return as the new position of the reader, whether
                // that's an XmlNodeType.EOF or the next Element.
                //
                // However, ReadSubtree() is just fine with stopping at an
                // EndElement.
                
                for (bool continueListening = true; continueListening;)
                {
                    reader.Read();
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.Name.Equals("message"))
                        {
                            using (XmlReader messageReader = reader.ReadSubtree())
                            {
                                Message msg = new Message(m_Room.m_Server, XElement.Load(messageReader));
                                continueListening = m_Callback(m_Room, msg);
                                messageReader.Close();
                            }
                            // At this point, we're now on the message EndElement.
                        }
                        else
                        {
                            throw new UnexpectedResponseException();
                        }
                    }
                }
            }
        }

        public void Listen(OnMessage messageCallback)
        {
            RoomListener roomListener = new RoomListener(this, messageCallback);
            m_Server.MakeApiCall("GET",
                                 "room/" + ID + "/live.xml",
                                 null,
                                 roomListener.ParseResponse);
        }
    }
}
