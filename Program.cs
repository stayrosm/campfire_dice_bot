using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace campfire_dice_bot
{
    class DiceRoller
    {
        private RNGCryptoServiceProvider m_rng;
        private byte[] m_data;
        private uint m_used;
        
        public DiceRoller()
        {
            m_rng = new RNGCryptoServiceProvider();
            m_data = new byte[128];
            m_used = 128;
        }

        public int RollSimple(int sides)
        {
            return RollSimple(sides, (sides == 100) ? 0 : 1);
        }

        public int RollSimple(int sides, int offset)
        {
            if (sides < 2 || sides > byte.MaxValue) { throw new ArgumentOutOfRangeException("sides", sides, "Sides must be in the range [2,255]"); }

            int max_fair_value = (byte.MaxValue / sides) * sides;
            for (;;) {
                if (m_data.Length == m_used)
                {
                    m_rng.GetBytes(m_data);
                    m_used = 0;
                }

                if (m_data[m_used++] < max_fair_value)
                {
                    return (m_data[m_used - 1] % sides) + offset;
                }
            }
        }

        public DiceRoll Roll(string desc)
        {
            return new DiceRoll(this, desc);
        }

        public DiceRoll Roll(int num_dice, int dice_size, int bonus)
        {
            return new DiceRoll(this, num_dice, dice_size, bonus);
        }
    }

    class DiceRoll
    {
        private static Regex dice_regex = new Regex(@"^(?<nd>\d+)?(d|D)(?<ds>\d+)((?<bs>\+|-)(?<bn>\d+))?$");

        public int Value { get; private set; }
        public string Description { get; private set; }

        public static bool LooksLikeASpec(string desc)
        {
            if (desc == null)
            {
                throw new ArgumentNullException("desc");
            }

            try
            {
                Match m = dice_regex.Match(desc);
                if ((m == null) || !m.Success)
                {
                    return false;
                }

                int num_dice = 1;
                if (m.Groups["nd"].Success)
                {
                    num_dice = Convert.ToInt32(m.Groups["nd"].Value);
                    if (0 == num_dice)
                    {
                        return false;
                    }
                }

                int dice_size = 0;
                if (m.Groups["ds"].Success)
                {
                    dice_size = Convert.ToInt32(m.Groups["ds"].Value);
                    if (dice_size < 2)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                int bonus = 0;
                if (m.Groups["bn"].Success)
                {
                    bonus = Convert.ToInt32(m.Groups["bn"].Value);
                    if (m.Groups["bs"].Success)
                    {
                        if (m.Groups["bs"].Value.Equals("-"))
                        {
                            bonus = -bonus;
                        }
                    }
                }
            }
            catch (FormatException)
            {
                return false;
            }

            return true;
        }

        public DiceRoll(DiceRoller roller, string desc)
        {
            if (desc == null)
            {
                throw new ArgumentNullException("desc");
            }

            Match m = dice_regex.Match(desc);
            if ((m == null) || !m.Success)
            {
                throw new ArgumentException("Not a valid roll spec", desc);
            }

            int num_dice = 1;
            if (m.Groups["nd"].Success)
            {
                num_dice = Convert.ToInt32(m.Groups["nd"].Value);
                if (0 == num_dice)
                {
                    throw new ArgumentException("Not a valid roll spec", desc);
                }
            }

            int dice_size = 0;
            if (m.Groups["ds"].Success)
            {
                dice_size = Convert.ToInt32(m.Groups["ds"].Value);
                if (dice_size < 2)
                {
                    throw new ArgumentException("Not a valid roll spec", desc);
                }
            }
            else
            {
                throw new ArgumentException("Not a valid roll spec", desc);
            }

            int bonus = 0;
            if (m.Groups["bn"].Success)
            {
                bonus = Convert.ToInt32(m.Groups["bn"].Value);
                if (m.Groups["bs"].Success)
                {
                    if (m.Groups["bs"].Value.Equals("-"))
                    {
                        bonus = -bonus;
                    }
                }
            }

            Init(roller, num_dice, dice_size, bonus);
        }

        public DiceRoll(DiceRoller roller, int num_dice, int dice_size, int bonus)
        {
            Init(roller, num_dice, dice_size, bonus);
        }

        private void Init(DiceRoller roller, int num_dice, int dice_size, int bonus)
        {
            if (roller == null)
            {
                throw new ArgumentNullException("roller");
            }
            if (num_dice < 1)
            {
                throw new ArgumentOutOfRangeException("num_dice", "num_dice must be >= 1");
            }
            if ((dice_size < 2) || (dice_size > 255))
            {
                throw new ArgumentOutOfRangeException("dice_size", "dice_size must be in the range [2,255]");
            }

            Value = 0;
            StringBuilder descBuilder = new StringBuilder();

            descBuilder.Append(num_dice);
            descBuilder.Append('d');
            descBuilder.Append(dice_size);
            if (bonus != 0)
            {
                descBuilder.Append((bonus < 0) ? '-' : '+');
                descBuilder.Append(Math.Abs(bonus));
            }

            descBuilder.Append(" ::= ");

            if (bonus != 0)
            {
                descBuilder.Append('(');
            }
            for (int i = 0; i < num_dice; ++i)
            {
                int roll = roller.RollSimple(dice_size);
                Value += roll;
                if (i > 0) { descBuilder.Append('+'); }
                descBuilder.Append(roll);
            }
            if (bonus != 0)
            {
                Value += bonus;
                descBuilder.Append(')');
                descBuilder.Append((bonus < 0) ? '-' : '+');
                descBuilder.Append(Math.Abs(bonus));
            }

            if ((bonus != 0) || (num_dice > 1))
            {
                descBuilder.Append(" = ");
                descBuilder.Append(Value);
            }

            Description = descBuilder.ToString();
        }
    }

    class Program
    {
        static readonly string SERVER = "lunis.campfirenow.com";
        static readonly string USERNAME = "lunisbot";
        static readonly string PASSWORD = "NO_THIS_DOESN'T_GET_CHECKED_IN";
        static readonly int ECLIPSE_PHASE_ROOM_ID = 329501;
        static readonly int BOT_TESTING_ROOM_ID = 330662;

        static DiceRoller m_dice = new DiceRoller();

        static void Main(string[] args)
        {            
            Campfire.Server server = new Campfire.Server(SERVER, USERNAME, PASSWORD);
            Campfire.Room room = server.GetRoomById(ECLIPSE_PHASE_ROOM_ID);
            room.Join();
            room.Listen(MessageCallback);
            room.Leave();
        }

        static bool MessageCallback(Campfire.Room room, Campfire.Message msg)
        {
            if (msg.Type == "TextMessage")
            {
                if (msg.Body.StartsWith("!roll") || msg.Body.StartsWith("!dice"))
                {
                    return DoDice(room, msg);
                }
                else if (msg.Body.StartsWith("!ep"))
                {
                    return DoEclipsePhaseDice(room, msg);
                }
                else if (msg.Body.StartsWith("!DEBUG_EPTEST"))
                {
                    StringBuilder response = new StringBuilder();
                    response.Append(GenerateEclipsePhaseResult(0, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(20, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(22, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(40, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(44, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(50, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(60, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(66, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(80, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(88, 50)); response.Append("\r\n");
                    response.Append(GenerateEclipsePhaseResult(99, 50)); response.Append("\r\n");
                    room.Speak("PasteMessage", response.ToString());
                    return true;
                }
                else if (msg.Body.StartsWith("!die"))
                {
                    return DoExit(room, msg);
                }
                else
                {
                    Console.WriteLine("RECV: {0} > {1}", msg.UserID.HasValue ? msg.User.Name : "", msg.Body);
                }
            }

            return true;
        }

        static bool DoDice(Campfire.Room room, Campfire.Message msg)
        {
            if (!msg.UserID.HasValue)
            {
                return true;
            }

            List<string> diceSpecs = new List<string>();
            string description = null;

            string[] diceParams = msg.Body.Remove(0, 5).Trim().Split(new char[] { ' ', '\t' });
            if (diceParams.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < diceParams.Length; ++i)
            {
                if (DiceRoll.LooksLikeASpec(diceParams[i]))
                {
                    diceSpecs.Add(diceParams[i]);
                }
                else
                {
                    if (i == 0)
                    {
                        return true;
                    }

                    description = diceParams.Skip(i).Aggregate((first, second) => first + " " + second);
                    break;
                }
            }

            StringBuilder response = new StringBuilder();

            foreach (string diceSpec in diceSpecs)
            {
                if (response.Length != 0)
                {
                    response.Append("\r\n");
                }
    
                response.Append("DICE (");
                response.Append(msg.User.Name);
                if (description != null)
                {
                    response.Append(' ');
                    response.Append(description);
                }
                response.Append(") ");
                response.Append(" ==> ");
                
                try
                {
                    DiceRoll roll = m_dice.Roll(diceSpec);
                    response.Append(roll.Description);
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }

            room.Speak(diceSpecs.Count() == 1 ? "TextMessage" : "PasteMessage", response.ToString());
            return true;
        }

        static string GenerateEclipsePhaseResult(int roll, int targetNumber)
        {
            bool isCritical = (roll % 11) == 0;
            bool isSuccess = (roll <= targetNumber) && (roll != 99);
            
            StringBuilder result = new StringBuilder();

            result.AppendFormat("{0:D2} vs {1:D2}", roll, targetNumber);
            result.Append(" -- ");

            if (isSuccess)
            {
                int mos = targetNumber - roll;
                if (0 == roll)
                {
                    result.Append("ULTIMATE SUCCESS!!!");
                }
                else if (isCritical)
                {
                    result.Append("CRITICAL SUCCESS!!!");
                }
                else if (mos >= 30)
                {
                    result.Append("Excellent Success!");
                }
                else
                {
                    result.Append("Success!");
                }

                if (mos > 0)
                {
                    result.AppendFormat(" (MoS = {0})", mos);
                }
            }
            else
            {
                int mof = roll - targetNumber;
                if (99 == roll)
                {
                    result.Append("ULTIMATE FAILURE!!");
                }
                else if (isCritical)
                {
                    result.Append("CRITICAL FAILURE");
                }
                else if (mof >= 30)
                {
                    result.Append("Severe Failure");
                }
                else
                {
                    result.Append("Failure");
                }

                if (mof > 0)
                {
                    result.AppendFormat(" (MoF = {0})", mof);
                }
            }

            return result.ToString();
        }

        static bool DoEclipsePhaseDice(Campfire.Room room, Campfire.Message msg)
        {
            if (!msg.UserID.HasValue)
            {
                return true;
            }

            int targetNum;
            string description = null;

            string[] diceParams = msg.Body.Remove(0, 3).Trim().Split(new char[] { ' ', '\t' });
            if (diceParams.Length == 0)
            {
                return true;
            }

            try
            {
                targetNum = Convert.ToInt32(diceParams[0]);
            }
            catch (FormatException)
            {
                return true;
            }

            if (targetNum < 0)
            {
                return true;
            }

            if (diceParams.Length > 1)
            {
                description = diceParams.Skip(1).Aggregate((first, second) => first + " " + second);
            }

            StringBuilder response = new StringBuilder("EPD (");
            response.Append(msg.User.Name);
            if (description != null)
            {
                response.Append(' ');
                response.Append(description);
            }
            response.Append(") ");
            response.Append(" ==> ");

            DiceRoll roll = m_dice.Roll(1,100,0);

            response.Append(GenerateEclipsePhaseResult(roll.Value, targetNum));

            room.Speak("TextMessage", response.ToString());
            return true;
        }

        static bool DoExit(Campfire.Room room, Campfire.Message msg)
        {
            if (msg.UserID.HasValue && (msg.UserID.Value == RYAN_USER_ID))
            {
                room.Speak("TextMessage", "Leaving.");
                return false;
            }

            return true;
        }
    }
}
