﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.Threading;

using MahApps.Metro;
using MahApps.Metro.Controls;

using SharpTox.Core;
using SharpTox.Av;

using Toxy.ViewModels;

using Path = System.IO.Path;
using Microsoft.Win32;

namespace Toxy
{
    public partial class MainWindow : MetroWindow
    {
        private Tox tox;
        private ToxAv toxav;
        private ToxCall call;

        private Dictionary<int, FlowDocument> convdic = new Dictionary<int, FlowDocument>();
        private Dictionary<int, FlowDocument> groupdic = new Dictionary<int, FlowDocument>();
        private List<FileTransfer> transfers = new List<FileTransfer>();

        private int current_number = 0;
        private Type current_type = typeof(FriendControl);
        private bool resizing = false;
        private bool focusTextbox = false;
        private bool typing = false;

        public MainWindow()
        {
            InitializeComponent();

            tox = new Tox(false);
            tox.Invoker = Dispatcher.BeginInvoke;
            tox.OnNameChange += tox_OnNameChange;
            tox.OnFriendMessage += tox_OnFriendMessage;
            tox.OnFriendAction += tox_OnFriendAction;
            tox.OnFriendRequest += tox_OnFriendRequest;
            tox.OnUserStatus += tox_OnUserStatus;
            tox.OnStatusMessage += tox_OnStatusMessage;
            tox.OnTypingChange += tox_OnTypingChange;
            tox.OnConnectionStatusChanged += tox_OnConnectionStatusChanged;
            tox.OnFileSendRequest += tox_OnFileSendRequest;
            tox.OnFileData += tox_OnFileData;
            tox.OnFileControl += tox_OnFileControl;

            tox.OnGroupInvite += tox_OnGroupInvite;
            tox.OnGroupMessage += tox_OnGroupMessage;
            tox.OnGroupAction += tox_OnGroupAction;
            tox.OnGroupNamelistChange += tox_OnGroupNamelistChange;

            toxav = new ToxAv(tox.GetPointer(), ToxAv.DefaultCodecSettings, 1);
            toxav.Invoker = Dispatcher.BeginInvoke;
            toxav.OnInvite += toxav_OnInvite;
            toxav.OnStart += toxav_OnStart;
            toxav.OnStarting += toxav_OnStart;
            toxav.OnEnd += toxav_OnEnd;
            toxav.OnEnding += toxav_OnEnd;
            toxav.OnPeerTimeout += toxav_OnEnd;
            toxav.OnRequestTimeout += toxav_OnEnd;
            toxav.OnReject += toxav_OnEnd;

            bool bootstrap_success = false;
            foreach (ToxNode node in nodes)
            {
                if (tox.BootstrapFromNode(node))
                    bootstrap_success = true;
            }

            if (File.Exists("data"))
            {
                if (!tox.Load("data"))
                {
                    MessageBox.Show("Could not load tox data, this program will now exit.", "Error");
                    Close();
                }
            }

            tox.Start();

            if (string.IsNullOrEmpty(tox.GetSelfName()))
                tox.SetName("Toxy User");

            Username.Text = tox.GetSelfName();

            Userstatus.Text = tox.GetSelfStatusMessage();
            StatusRectangle.Fill = new SolidColorBrush(Colors.LimeGreen);

            SetStatus(null);
            InitFriends();
            if (tox.GetFriendlistCount() > 0)
                SelectFriendControl(GetFriendControlByNumber(0));
        }

        private void toxav_OnEnd(int call_index, IntPtr args)
        {
            if (call == null)
                return;

            EndCall();
            CallButton.Visibility = Visibility.Visible;
            HangupButton.Visibility = Visibility.Hidden;
        }

        private void toxav_OnStart(int call_index, IntPtr args)
        {
            if (call != null)
                call.Start();

            int friendnumber = toxav.GetPeerID(call_index, 0);
            FriendControl callingFriend = GetFriendControlByNumber(friendnumber);
            callingFriend.CallButtonGrid.Visibility = Visibility.Collapsed;
            CallButton.Visibility = Visibility.Hidden;

            if (current_number == friendnumber)
                HangupButton.Visibility = Visibility.Visible;

            AddCallControl(friendnumber, "{0}");
        }

        private void toxav_OnInvite(int call_index, IntPtr args)
        {
            //TODO: notify the user of another incoming call
            if (call != null)
                return;

            int friendnumber = toxav.GetPeerID(call_index, 0);
            FriendControl control = GetFriendControlByNumber(friendnumber);

            if (control == null)
                return;

            control.CallButtonGrid.Visibility = Visibility.Visible;
            control.AcceptCallButton.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (call != null)
                    return;

                call = new ToxCall(tox, toxav, call_index, friendnumber);
                call.Answer();
            };

            control.DenyCallButton.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (call == null)
                {
                    toxav.Reject(call_index, "I'm busy...");
                    control.CallButtonGrid.Visibility = Visibility.Collapsed;
                }
                else
                {
                    call.Stop();
                    call = null;
                }
            };
        }

        private void tox_OnGroupNamelistChange(int groupnumber, int peernumber, ToxChatChange change)
        {
            GroupControl control = GetGroupControlByNumber(groupnumber);
            if (control == null)
                return;

            if (change == ToxChatChange.PEER_ADD || change == ToxChatChange.PEER_DEL)
            {
                string status = string.Format("Peers online: {0}", tox.GetGroupMemberCount(groupnumber));
                control.SetStatusMessage(status);
            }

            if (current_number == groupnumber && current_type == typeof(GroupControl))
                Friendstatus.Text = string.Join(", ", tox.GetGroupNames(groupnumber));//Friendstatus.Text = status;
        }

        private void tox_OnGroupAction(int groupnumber, int friendgroupnumber, string action)
        {
            MessageData data = new MessageData() { Username = "*", Message = string.Format("{0} {1}", tox.GetGroupMemberName(groupnumber, friendgroupnumber), action) };

            if (groupdic.ContainsKey(groupnumber))
                groupdic[groupnumber].AddNewMessageRow(tox, data);
            else
            {
                FlowDocument document = GetNewFlowDocument();
                groupdic.Add(groupnumber, document);
                groupdic[groupnumber].AddNewMessageRow(tox, data);
            }

            if (!(current_number == groupnumber && current_type == typeof(GroupControl)))
                GetGroupControlByNumber(groupnumber).NewMessageIndicator.Fill = (Brush)FindResource("AccentColorBrush");

            if (current_number == groupnumber && current_type == typeof(GroupControl))
                ScrollChatBox();

            this.Flash();
        }

        private void tox_OnGroupMessage(int groupnumber, int friendgroupnumber, string message)
        {
            MessageData data = new MessageData() { Username = tox.GetGroupMemberName(groupnumber, friendgroupnumber), Message = message };

            if (groupdic.ContainsKey(groupnumber))
            {
                Run run = GetLastMessageRun(groupdic[groupnumber]);

                if (run != null)
                {
                    if (run.Text == data.Username)
                        groupdic[groupnumber].AppendMessage(data);
                    else
                        groupdic[groupnumber].AddNewMessageRow(tox, data);
                }
                else
                {
                    groupdic[groupnumber].AddNewMessageRow(tox, data);
                }
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                groupdic.Add(groupnumber, document);
                groupdic[groupnumber].AddNewMessageRow(tox, data);
            }

            if (!(current_number == groupnumber && current_type == typeof(GroupControl)))
                GetGroupControlByNumber(groupnumber).NewMessageIndicator.Fill = (Brush)FindResource("AccentColorBrush");

            if (current_number == groupnumber && current_type == typeof(GroupControl))
                ScrollChatBox();

            this.Flash();
        }

        private void tox_OnGroupInvite(int friendnumber, string group_public_key)
        {
            //auto join groupchats for now

            int groupnumber = tox.JoinGroup(friendnumber, group_public_key);

            if (groupnumber != -1)
                AddGroupToView(groupnumber);
        }

        private void tox_OnFriendRequest(string id, string message)
        {
            try { AddFriendRequestToView(id, message); }
            catch (Exception ex) { MessageBox.Show(ex.ToString()); }
        }

        private void tox_OnFileControl(int friendnumber, int receive_send, int filenumber, int control_type, byte[] data)
        {
            switch ((ToxFileControl)control_type)
            {
                case ToxFileControl.FINISHED:
                    {
                        FileTransfer ft = GetFileTransfer(friendnumber, filenumber);

                        if (ft == null)
                            return;

                        ft.Stream.Close();
                        ft.Stream = null;

                        ft.Control.TransferFinished();
                        ft.Control.SetStatus("Finished!");
                        ft.Finished = true;

                        transfers.Remove(ft);
                        break;
                    }

                case ToxFileControl.ACCEPT:
                    {
                        FileTransfer ft = GetFileTransfer(friendnumber, filenumber);
                        ft.Control.SetStatus("Transferring....");
                        ft.Stream = new FileStream(ft.FileName, FileMode.Open);
                        ft.Thread = new Thread(transferFile);
                        ft.Thread.Start(ft);

                        break;
                    }

                case ToxFileControl.KILL:
                    {
                        FileTransfer transfer = GetFileTransfer(friendnumber, filenumber);
                        transfer.Finished = true;
                        if (transfer != null)
                        {
                            if (transfer.Stream != null)
                                transfer.Stream.Close();

                            if (transfer.Thread != null)
                            {
                                transfer.Thread.Abort();
                                transfer.Thread.Join();
                            }

                            transfer.Control.HideAllButtons();
                            transfer.Control.SetStatus("Transfer killed!");
                        }

                        break;
                    }
            }
        }

        private void transferFile(object ft)
        {
            FileTransfer transfer = (FileTransfer)ft;

            IntPtr ptr = tox.GetPointer();
            int chunk_size = tox.FileDataSize(transfer.FriendNumber);
            byte[] buffer = new byte[chunk_size];

            while (true)
            {
                ulong remaining = tox.FileDataRemaining(transfer.FriendNumber, transfer.FileNumber, 0);
                if (remaining > (ulong)chunk_size)
                {
                    if (transfer.Stream.Read(buffer, 0, chunk_size) == 0)
                        break;

                    while (!tox.FileSendData(transfer.FriendNumber, transfer.FileNumber, buffer))
                    {
                        int time = (int)ToxFunctions.DoInterval(ptr);

                        Console.WriteLine("Could not send data, sleeping for {0}ms", time);
                        Thread.Sleep(time);
                    }

                    Console.WriteLine("Data sent: {0} bytes", buffer.Length);
                }
                else
                {
                    buffer = new byte[remaining];

                    if (transfer.Stream.Read(buffer, 0, (int)remaining) == 0)
                        break;

                    tox.FileSendData(transfer.FriendNumber, transfer.FileNumber, buffer);

                    Console.WriteLine("Sent the last chunk of data: {0} bytes", buffer.Length);
                }

                double value = (double)remaining / (double)transfer.FileSize;
                transfer.Control.SetProgress(100 - (int)(value * 100));
            }

            transfer.Stream.Close();
            tox.FileSendControl(transfer.FriendNumber, 0, transfer.FileNumber, ToxFileControl.FINISHED, new byte[0]);

            transfer.Control.HideAllButtons();
            transfer.Control.SetStatus("Finished!");
            transfer.Finished = true;
        }

        private FileTransfer GetFileTransfer(int friendnumber, int filenumber)
        {
            foreach (FileTransfer ft in transfers)
                if (ft.FileNumber == filenumber && ft.FriendNumber == friendnumber && !ft.Finished)
                    return ft;

            return null;
        }

        private void tox_OnFileData(int friendnumber, int filenumber, byte[] data)
        {
            FileTransfer ft = GetFileTransfer(friendnumber, filenumber);

            if (ft == null)
                return;

            if (ft.Stream == null)
                throw new NullReferenceException("Unexpectedly received data");

            ulong remaining = tox.FileDataRemaining(friendnumber, filenumber, 1);
            double value = (double)remaining / (double)ft.FileSize;

            ft.Control.SetProgress(100 - (int)(value * 100));
            ft.Control.SetStatus(string.Format("{0}/{1}", ft.FileSize - remaining, ft.FileSize));

            if (ft.Stream.CanWrite)
                ft.Stream.Write(data, 0, data.Length);
        }

        private void tox_OnFileSendRequest(int friendnumber, int filenumber, ulong filesize, string filename)
        {
            if (!convdic.ContainsKey(friendnumber))
                convdic.Add(friendnumber, GetNewFlowDocument());

            FileTransfer transfer = convdic[friendnumber].AddNewFileTransfer(tox, friendnumber, filenumber, filename, filesize, false);

            FriendControl control = GetFriendControlByNumber(friendnumber);
            if (control != null)
                if (!(current_number == friendnumber && current_type == typeof(FriendControl)))
                    control.NewMessageIndicator.Fill = (Brush)FindResource("AccentColorBrush");

            transfer.Control.OnAccept += delegate(int friendnum, int filenum)
            {
                if (transfer.Stream != null)
                    return;

                SaveFileDialog dialog = new SaveFileDialog();
                dialog.FileName = filename;

                if (dialog.ShowDialog() == true) //guess what, this bool is nullable
                {
                    transfer.Stream = new FileStream(dialog.FileName, FileMode.Create);
                    tox.FileSendControl(friendnumber, 1, filenumber, ToxFileControl.ACCEPT, new byte[0]);
                }
            };

            transfer.Control.OnDecline += delegate(int friendnum, int filenum)
            {
                if (!transfer.IsSender)
                    tox.FileSendControl(friendnumber, 1, filenumber, ToxFileControl.KILL, new byte[0]);
                else
                    tox.FileSendControl(friendnumber, 0, filenumber, ToxFileControl.KILL, new byte[0]);

                if (transfer.Thread != null)
                {
                    transfer.Thread.Abort();
                    transfer.Thread.Join();
                }

                if (transfer.Stream != null)
                    transfer.Stream.Close();

            };

            transfer.Control.OnFileOpen += delegate()
            {
                try { Process.Start(transfer.FileName); }
                catch { /*want to open a "choose program dialog" here*/ }
            };

            transfer.Control.OnFolderOpen += delegate()
            {
                string filePath = Path.Combine(Environment.CurrentDirectory, filename);
                Process.Start("explorer.exe", @"/select, " + filePath);
            };

            transfers.Add(transfer);

            this.Flash();
        }

        private void tox_OnConnectionStatusChanged(int friendnumber, int status)
        {
            if (status == 0)
            {
                DateTime lastOnline = tox.GetLastOnline(friendnumber);
                FriendControl control = GetFriendControlByNumber(friendnumber);

                if (control == null)
                    return;

                control.SetStatusMessage("Last seen: " + lastOnline.ToShortDateString() + " " + lastOnline.ToLongTimeString());
                control.SetStatus(ToxUserStatus.INVALID); //not the proper way to do it, I know...

                if (current_number == friendnumber && current_type == typeof(FriendControl))
                {
                    CallButton.Visibility = Visibility.Hidden;
                    FileButton.Visibility = Visibility.Hidden;
                }
            }
            else
            {
                if (current_number == friendnumber && current_type == typeof(FriendControl))
                {
                    CallButton.Visibility = Visibility.Visible;
                    FileButton.Visibility = Visibility.Visible;
                }
            }
        }

        private void tox_OnTypingChange(int friendnumber, bool is_typing)
        {
            if (current_number == friendnumber && current_type == typeof(FriendControl))
            {
                if (is_typing)
                    TypingStatusLabel.Content = tox.GetName(friendnumber) + " is typing...";
                else
                    TypingStatusLabel.Content = "";
            }
        }

        private void tox_OnFriendAction(int friendnumber, string action)
        {
            MessageData data = new MessageData() { Username = "*", Message = string.Format("{0} {1}", tox.GetName(friendnumber), action) };

            if (convdic.ContainsKey(friendnumber))
                convdic[friendnumber].AddNewMessageRow(tox, data);
            else
            {
                FlowDocument document = GetNewFlowDocument();
                convdic.Add(friendnumber, document);
                convdic[friendnumber].AddNewMessageRow(tox, data);
            }

            if (!(current_number == friendnumber && current_type == typeof(FriendControl)))
                GetFriendControlByNumber(friendnumber).NewMessageIndicator.Fill = (Brush)FindResource("AccentColorBrush");

            if (current_number == friendnumber && current_type == typeof(FriendControl))
                ScrollChatBox();

            this.Flash();
        }

        private FlowDocument GetNewFlowDocument()
        {
            Stream doc_stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Toxy.Message.xaml");
            FlowDocument doc = (FlowDocument)XamlReader.Load(doc_stream);
            doc.IsEnabled = true;

            return doc;
        }

        private void tox_OnFriendMessage(int friendnumber, string message)
        {
            MessageData data = new MessageData() { Username = tox.GetName(friendnumber), Message = message };

            if (convdic.ContainsKey(friendnumber))
            {
                Run run = GetLastMessageRun(convdic[friendnumber]);

                if (run != null)
                {
                    if (run.Text == tox.GetName(friendnumber))
                        convdic[friendnumber].AppendMessage(data);
                    else
                        convdic[friendnumber].AddNewMessageRow(tox, data);
                }
                else
                {
                    convdic[friendnumber].AddNewMessageRow(tox, data);
                }
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                convdic.Add(friendnumber, document);
                convdic[friendnumber].AddNewMessageRow(tox, data);
            }

            if (!(current_number == friendnumber && current_type == typeof(FriendControl)))
                GetFriendControlByNumber(friendnumber).NewMessageIndicator.Fill = (Brush)FindResource("AccentColorBrush");

            if (current_number == friendnumber && current_type == typeof(FriendControl))
                ScrollChatBox();

            this.Flash();
        }

        private void ScrollChatBox()
        {
            ScrollViewer viewer = FindScrollViewer(ChatBox);

            if (viewer != null)
                viewer.ScrollToBottom();
        }

        private static ScrollViewer FindScrollViewer(FlowDocumentScrollViewer viewer)
        {
            if (VisualTreeHelper.GetChildrenCount(viewer) == 0)
                return null;

            DependencyObject first = VisualTreeHelper.GetChild(viewer, 0);
            if (first == null)
                return null;

            Decorator border = (Decorator)VisualTreeHelper.GetChild(first, 0);
            if (border == null)
                return null;

            return (ScrollViewer)border.Child;
        }

        private Run GetLastMessageRun(FlowDocument doc)
        {
            try
            {
                Paragraph para = (Paragraph)doc.FindChildren<TableRow>()
                    .Last()
                    .FindChildren<TableCell>()
                    .First()
                    .Blocks.FirstBlock;

                Run run = (Run)para.Inlines.FirstInline;
                return run;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        private void NewGroupButton_Click(object sender, RoutedEventArgs e)
        {
            int groupnumber = tox.NewGroup();

            if (groupnumber != -1)
                AddGroupToView(groupnumber);
        }

        private FriendControl GetFriendControlByNumber(int friendnumber)
        {
            foreach (FriendControl control in FriendWrapper.FindChildren<FriendControl>())
                if (control.FriendNumber == friendnumber)
                    return control;

            return null;
        }

        private GroupControl GetGroupControlByNumber(int groupnumber)
        {
            foreach (GroupControl control in FriendWrapper.FindChildren<GroupControl>())
                if (control.GroupNumber == groupnumber)
                    return control;

            return null;
        }

        private void tox_OnUserStatus(int friendnumber, ToxUserStatus status)
        {
            FriendControl control = GetFriendControlByNumber(friendnumber);
            control.SetStatus(status);
        }

        private void tox_OnStatusMessage(int friendnumber, string newstatus)
        {
            FriendControl control = GetFriendControlByNumber(friendnumber);
            control.SetStatusMessage(newstatus);

            if (current_number == friendnumber && current_type == typeof(FriendControl))
                Friendstatus.Text = newstatus;
        }

        private void tox_OnNameChange(int friendnumber, string newname)
        {
            FriendControl control = GetFriendControlByNumber(friendnumber);
            control.SetUsername(newname);

            if (current_number == friendnumber && current_type == typeof(FriendControl))
                Friendname.Text = newname;
        }

        private ToxNode[] nodes = new ToxNode[] 
        { 
            new ToxNode("192.254.75.98", 33445, "951C88B7E75C867418ACDB5D273821372BB5BD652740BCDF623A4FA293E75D2F", false),
            new ToxNode("37.187.46.132", 33445, "A9D98212B3F972BD11DA52BEB0658C326FCCC1BFD49F347F9C2D3D8B61E1B927", false),
            new ToxNode("54.199.139.199", 33445, "7F9C31FE850E97CEFD4C4591DF93FC757C7C12549DDD55F8EEAECC34FE76C029", false) 
        };

        private void InitFriends()
        {
            //Creates a new FriendControl for every friend
            foreach (int FriendNumber in tox.GetFriendlist())
                AddFriendToView(FriendNumber);
        }

        private void AddGroupToView(int groupnumber)
        {
            string groupname = string.Format("Groupchat #{0}", groupnumber);
            //string groupstatus = string.Format("Peers online: {0}", tox.GetGroupMemberCount(groupnumber));

            GroupControl group = new GroupControl(groupnumber);
            group.GroupNameLabel.Content = groupname;
            group.GroupStatusLabel.Content = string.Join(", ", tox.GetGroupNames(groupnumber));
            group.Click += group_Click;
            FriendWrapper.Children.Add(group);

            MenuItem item = new MenuItem();
            item.Header = "Delete";
            item.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (group != null)
                {
                    FriendWrapper.Children.Remove(group);
                    group = null;

                    if (groupdic.ContainsKey(groupnumber))
                    {
                        groupdic.Remove(groupnumber);

                        if (current_number == groupnumber && current_type == typeof(GroupControl))
                            ChatBox.Document = null;
                    }

                    tox.DeleteGroupChat(groupnumber);
                }
            };

            group.ContextMenu = new ContextMenu();
            group.ContextMenu.Items.Add(item);
        }

        private void group_Click(object sender, RoutedEventArgs e)
        {
            GroupControl control = (GroupControl)sender;

            TypingStatusLabel.Content = "";

            if (!control.Selected)
            {
                SelectGroupControl(control);
                ScrollChatBox();
            }
        }

        private void AddFriendToView(int friendNumber)
        {
            string friendName = tox.GetName(friendNumber);
            string friendStatus;

            if (tox.GetFriendConnectionStatus(friendNumber) != 0)
            {
                friendStatus = tox.GetStatusMessage(friendNumber);
            }
            else
            {
                DateTime lastOnline = tox.GetLastOnline(friendNumber);
                friendStatus = "Last seen: " + lastOnline.ToShortDateString() + " " + lastOnline.ToLongTimeString();
            }

            if (string.IsNullOrEmpty(friendName))
                friendName = tox.GetClientID(friendNumber);

            FriendControl friend = new FriendControl(friendNumber);
            friend.FriendNameLabel.Text = friendName;
            friend.FriendStatusLabel.Text = friendStatus;
            friend.SetStatus(ToxUserStatus.INVALID);
            friend.Click += friend_Click;
            friend.FocusTextBox += friend_FocusTextBox;
            FriendWrapper.Children.Add(friend);

            MenuItem item = new MenuItem();
            item.Header = "Delete";
            item.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (friend != null)
                {
                    FriendWrapper.Children.Remove(friend);
                    friend = null;

                    if (convdic.ContainsKey(friendNumber))
                    {
                        convdic.Remove(friendNumber);

                        if (current_number == friendNumber && current_type == typeof(FriendControl))
                            ChatBox.Document = null;
                    }

                    tox.DeleteFriend(friendNumber);
                }
            };

            MenuItem copyIDMenuitem = new MenuItem();
            copyIDMenuitem.Header = "Copy ID";
            copyIDMenuitem.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (friend != null)
                {
                    Clipboard.Clear();
                    Clipboard.SetText(tox.GetClientID(friendNumber));
                }
            };

            MenuItem item2 = new MenuItem();
            item2.Header = "Invite";
            item2.Visibility = Visibility.Collapsed;

            friend.ContextMenu = new ContextMenu();
            friend.ContextMenu.Items.Add(item);
            friend.ContextMenu.Items.Add(item2);
            friend.ContextMenu.Items.Add(copyIDMenuitem);
            friend.ContextMenuOpening += delegate(object sender, ContextMenuEventArgs e)
            {
                item2.Items.Clear();
                GroupControl[] groupcontrols = FriendWrapper.FindChildren<GroupControl>().ToArray<GroupControl>();
                if (groupcontrols.Length > 0)
                {
                    item2.Visibility = Visibility.Visible;
                    foreach (GroupControl control in groupcontrols)
                    {
                        MenuItem groupitem = new MenuItem();
                        groupitem.Header = control.GroupNameLabel.Content;
                        groupitem.Click += delegate(object s, RoutedEventArgs e2)
                        {
                            tox.InviteFriend(friendNumber, control.GroupNumber);
                        };

                        item2.Items.Add(groupitem);
                    }
                }
                else
                {
                    item2.Visibility = Visibility.Collapsed;
                }
            };
        }

        private void friend_FocusTextBox(object sender, RoutedEventArgs e)
        {
            TextToSend.Focus();
        }

        private void AddFriendRequestToView(string id, string message)
        {
            string friendName = id;

            FriendControl friend = new FriendControl();
            friend.FriendNameLabel.Text = friendName;
            friend.RequestButtonGrid.Visibility = Visibility.Visible;
            friend.AcceptButton.Click += (sender, e) => AcceptButton_Click(id, friend);
            friend.DeclineButton.Click += (sender, e) => DeclineButton_Click(friend);
            friend.FriendStatusLabel.Visibility = Visibility.Collapsed;

            MessageData messageData = new MessageData() { Message = message, Username = "Request Message" };
            friend.RequestFlowDocument = GetNewFlowDocument();
            friend.Click += (sender, e) => FriendRequest_Click(friend, messageData);

            NotificationWrapper.Children.Add(friend);

            if (ListViewTabControl.SelectedIndex != 1)
                RequestsTabItem.Header = "Requests*";
        }

        private void FriendRequest_Click(FriendControl friendControl, MessageData messageData)
        {
            friendControl.RequestFlowDocument.AddNewMessageRow(tox, messageData);
        }

        private void AcceptButton_Click(string id, FriendControl friendControl)
        {
            int friendnumber = tox.AddFriendNoRequest(id);
            tox.SetSendsReceipts(friendnumber, true);
            AddFriendToView(friendnumber);
            NotificationWrapper.Children.Remove(friendControl);
        }

        private void DeclineButton_Click(FriendControl friendControl)
        {
            NotificationWrapper.Children.Remove(friendControl);
        }

        private void SelectGroupControl(GroupControl group)
        {
            Grid grid = (Grid)group.FindName("MainGrid");
            group.Selected = true;
            grid.SetResourceReference(Grid.BackgroundProperty, "AccentColorBrush3");

            int friendNumber = group.GroupNumber;

            foreach (FriendControl control in FriendWrapper.FindChildren<FriendControl>())
            {
                Grid grid1 = (Grid)control.FindName("MainGrid");
                control.Selected = false;

                grid1.Background = new SolidColorBrush(Colors.Transparent);

                control.FriendNameLabel.Foreground = new SolidColorBrush(Colors.Black);
                control.FriendStatusLabel.Foreground = new SolidColorBrush(Colors.Black);
            }

            foreach (GroupControl control in FriendWrapper.FindChildren<GroupControl>())
            {
                if (group != control)
                {
                    Grid grid1 = (Grid)control.FindName("MainGrid");
                    control.Selected = false;
                    grid1.Background = new SolidColorBrush(Colors.Transparent);
                    control.GroupNameLabel.Foreground = new SolidColorBrush(Colors.Black);
                    control.GroupStatusLabel.Foreground = new SolidColorBrush(Colors.Black);
                }
                else
                {
                    control.GroupNameLabel.Foreground = new SolidColorBrush(Colors.White);
                    control.GroupStatusLabel.Foreground = new SolidColorBrush(Colors.White);
                }
            }

            CallButton.Visibility = Visibility.Hidden;
            FileButton.Visibility = Visibility.Hidden;

            Friendname.Text = string.Format("Groupchat #{0}", group.GroupNumber);
            Friendstatus.Text = string.Join(", ", tox.GetGroupNames(group.GroupNumber));//string.Format("Peers online: {0}", tox.GetGroupMemberCount(group.GroupNumber));

            current_type = typeof(GroupControl);
            current_number = group.GroupNumber;

            if (groupdic.ContainsKey(current_number))
            {
                ChatBox.Document = groupdic[current_number];
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                groupdic.Add(current_number, document);
                ChatBox.Document = groupdic[current_number];
            }
        }

        private void AddCallControl(int friendnumber, string status)
        {
            if (ChatGrid.Children[0].GetType() == typeof(CallControl))
                ChatGrid.Children.RemoveAt(0);

            CallControl callControl = new CallControl();
            callControl.SetLabel(string.Format(status, tox.GetName(friendnumber)));
            callControl.HangupButton.Click += (sender, e) => HangupButton_Click();
            ChatGrid.Children.Insert(0, callControl);
        }

        private void HangupButton_Click()
        {
            if (call == null)
                return;

            EndCall();
        }

        private void EndCall()
        {
            call.Stop();

            int friendnumber = toxav.GetPeerID(call.CallIndex, 0);
            FriendControl control = GetFriendControlByNumber(friendnumber);

            if (control != null)
                control.CallButtonGrid.Visibility = Visibility.Collapsed;

            call = null;
            ChatGrid.Children.RemoveAt(0);

            HangupButton.Visibility = Visibility.Collapsed;
            CallButton.Visibility = Visibility.Visible;
        }

        private void SelectFriendControl(FriendControl friend)
        {
            Grid grid = (Grid)friend.FindName("MainGrid");
            friend.Selected = true;
            //grid.Background = new SolidColorBrush(Color.FromRgb(236, 236, 236));

            grid.SetResourceReference(Grid.BackgroundProperty, "AccentColorBrush3"); 

            int friendNumber = friend.FriendNumber;
            
            foreach (FriendControl control in FriendWrapper.FindChildren<FriendControl>())
            {
                if (friend != control)
                {
                    Grid grid1 = (Grid)control.FindName("MainGrid");
                    control.Selected = false;
                    grid1.Background = new SolidColorBrush(Colors.Transparent);
                    control.FriendNameLabel.Foreground = new SolidColorBrush(Colors.Black);
                    control.FriendStatusLabel.Foreground = new SolidColorBrush(Colors.Black);
                }
                else
                {
                    control.FriendNameLabel.Foreground = new SolidColorBrush(Colors.White);
                    control.FriendStatusLabel.Foreground = new SolidColorBrush(Colors.White);
                }
            }

            foreach (GroupControl control in FriendWrapper.FindChildren<GroupControl>())
            {
                Grid grid1 = (Grid)control.FindName("MainGrid");
                control.Selected = false;
                grid1.Background = new SolidColorBrush(Colors.Transparent);
                control.GroupNameLabel.Foreground = new SolidColorBrush(Colors.Black);
                control.GroupStatusLabel.Foreground = new SolidColorBrush(Colors.Black);
            }

            Friendname.Text = tox.GetName(friendNumber);
            Friendstatus.Text = tox.GetStatusMessage(friendNumber);
            if (call != null)
            {
                if (call.FriendNumber != friendNumber)
                    HangupButton.Visibility = Visibility.Hidden;
                else
                    HangupButton.Visibility = Visibility.Visible;
            }
            else
            {
                if (tox.GetFriendConnectionStatus(friendNumber) != 1)
                {
                    CallButton.Visibility = Visibility.Hidden;
                    FileButton.Visibility = Visibility.Hidden;
                }
                else
                {
                    CallButton.Visibility = Visibility.Visible;
                    FileButton.Visibility = Visibility.Visible;
                }
            }

            current_type = typeof(FriendControl);
            current_number = friend.FriendNumber;

            if (convdic.ContainsKey(current_number))
            {
                ChatBox.Document = convdic[current_number];
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                convdic.Add(current_number, document);
                ChatBox.Document = convdic[current_number];
            }
        }

        private void friend_Click(object sender, RoutedEventArgs e)
        {
            FriendControl control = (FriendControl)sender;

            if (!tox.GetIsTyping(control.FriendNumber))
                TypingStatusLabel.Content = "";
            else
                TypingStatusLabel.Content = tox.GetName(control.FriendNumber) + " is typing...";

            if (!control.Selected)
            {
                SelectFriendControl(control);
                ScrollChatBox();
            }
        }

        private void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (call != null)
                call.Stop();

            foreach(FileTransfer transfer in transfers)
            {
                if (transfer.Thread != null)
                {
                    //TODO: show a message warning the users that there are still file transfers in progress
                    transfer.Thread.Abort();
                    transfer.Thread.Join();
                }
            }

            toxav.Kill();

            tox.Save("data");
            tox.Kill();
        }

        private void OpenAddFriend_Click(object sender, RoutedEventArgs e)
        {
            FriendFlyout.IsOpen = !FriendFlyout.IsOpen;
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsUsername.Text = tox.GetSelfName();
            SettingsStatus.Text = tox.GetSelfStatusMessage();
            SettingsNospam.Text = tox.GetNospam().ToString();
            SettingsFlyout.IsOpen = !SettingsFlyout.IsOpen;
        }

        private void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            TextRange message = new TextRange(AddFriendMessage.Document.ContentStart, AddFriendMessage.Document.ContentEnd);

            if (!(!string.IsNullOrEmpty(AddFriendID.Text) && !string.IsNullOrEmpty(message.Text)))
                return;

            if (AddFriendID.Text.Contains("@"))
            {
                try
                {
                    string id = DnsTools.DiscoverToxID(AddFriendID.Text);
                    AddFriendID.Text = id;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not find a tox id:\n" + ex.ToString());
                }

                return;
            }

            int friendnumber;
            try
            {
                friendnumber = tox.AddFriend(AddFriendID.Text, message.Text);
                FriendFlyout.IsOpen = false;
                AddFriendToView(friendnumber);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
                return;
            }

            AddFriendID.Text = string.Empty;
            AddFriendMessage.Document.Blocks.Clear();
            AddFriendMessage.Document.Blocks.Add(new Paragraph(new Run("Hello, I'd like to add you to my friends list.")));

            FriendFlyout.IsOpen = false;
        }

        private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            tox.SetName(SettingsUsername.Text);
            tox.SetStatusMessage(SettingsStatus.Text);

            uint nospam;
            if (uint.TryParse(SettingsNospam.Text, out nospam))
                tox.SetNospam(nospam);

            Username.Text = SettingsUsername.Text;
            Userstatus.Text = SettingsStatus.Text;

            SettingsFlyout.IsOpen = false;

            if (AccentComboBox.SelectedItem != null)
            {
                var theme = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
                var accent = ThemeManager.GetAccent(((AccentColorMenuData)AccentComboBox.SelectedItem).Name);
                ThemeManager.ChangeAppStyle(System.Windows.Application.Current, accent, theme.Item1);
            }
            if (AppThemeComboBox.SelectedItem != null)
            {
                var theme = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
                var appTheme = ThemeManager.GetAppTheme(((AppThemeMenuData)AppThemeComboBox.SelectedItem).Name);
                ThemeManager.ChangeAppStyle(System.Windows.Application.Current, theme.Item2, appTheme);
            }
        }

        private void TextToSend_KeyDown(object sender, KeyEventArgs e)
        {
            string text = TextToSend.Text;

            if (e.Key == Key.Enter)
            {
                if (Keyboard.IsKeyDown(Key.LeftShift))
                {
                    TextToSend.Text += Environment.NewLine;
                    TextToSend.CaretIndex = TextToSend.Text.Length;
                    return;
                }

                if (e.IsRepeat)
                    return;

                if (string.IsNullOrEmpty(text))
                    return;

                if (tox.GetFriendConnectionStatus(current_number) == 0 && current_type == typeof(FriendControl))
                    return;

                if (text.StartsWith("/me "))
                {
                    //action
                    string action = text.Substring(4);
                    int messageid = -1;

                    if (current_type == typeof(FriendControl))
                        messageid = tox.SendAction(current_number, action);
                    else if (current_type == typeof(GroupControl))
                        tox.SendGroupAction(current_number, action);

                    MessageData data = new MessageData() { Username = "*", Message = string.Format("{0} {1}", tox.GetSelfName(), action) };

                    if (current_type == typeof(FriendControl))
                    {
                        if (convdic.ContainsKey(current_number))
                        {
                            convdic[current_number].AddNewMessageRow(tox, data);
                        }
                        else
                        {
                            FlowDocument document = GetNewFlowDocument();
                            convdic.Add(current_number, document);
                            convdic[current_number].AddNewMessageRow(tox, data);
                        }
                    }
                }
                else
                {
                    //regular message
                    string message = text;

                    int messageid = -1;

                    if (current_type == typeof(FriendControl))
                        messageid = tox.SendMessage(current_number, message);
                    else if (current_type == typeof(GroupControl))
                        tox.SendGroupMessage(current_number, message);

                    MessageData data = new MessageData() { Username = tox.GetSelfName(), Message = message };

                    if (current_type == typeof(FriendControl))
                    {
                        if (convdic.ContainsKey(current_number))
                        {
                            Run run = GetLastMessageRun(convdic[current_number]);
                            if (run != null)
                            {
                                if (run.Text == data.Username)
                                    convdic[current_number].AppendMessage(data);
                                else
                                    convdic[current_number].AddNewMessageRow(tox, data);
                            }
                            else
                            {
                                convdic[current_number].AddNewMessageRow(tox, data);
                            }
                        }
                        else
                        {
                            FlowDocument document = GetNewFlowDocument();
                            convdic.Add(current_number, document);
                            convdic[current_number].AddNewMessageRow(tox, data);
                        }
                    }
                }

                ScrollChatBox();

                TextToSend.Text = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && current_type == typeof(GroupControl))
            {
                string[] names = tox.GetGroupNames(current_number);

                foreach (string name in names)
                {
                    if (!name.ToLower().StartsWith(text.ToLower()))
                        continue;

                    TextToSend.Text = string.Format("{0}, ", name);
                    TextToSend.SelectionStart = TextToSend.Text.Length;
                }

                e.Handled = true;
            }
        }

        private void GithubButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://github.com/Reverp/Toxy-WPF");
        }

        private void TextToSend_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (current_type != typeof(FriendControl))
                return;

            string text = TextToSend.Text;

            if (string.IsNullOrEmpty(text))
            {
                if (typing)
                {
                    typing = false;
                    tox.SetUserIsTyping(current_number, typing);
                }
            }
            else
            {
                if (!typing)
                {
                    typing = true;
                    tox.SetUserIsTyping(current_number, typing);
                }
            }
        }

        private void CopyIDButton_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(tox.GetAddress());
        }

        private void MetroWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!resizing && focusTextbox)
                TextToSend.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        }

        private void TextToSend_OnGotFocus(object sender, RoutedEventArgs e)
        {
            focusTextbox = true;
        }

        private void TextToSend_OnLostFocus(object sender, RoutedEventArgs e)
        {
            focusTextbox = false;
        }

        private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (resizing)
            {
                resizing = false;
                if (focusTextbox)
                {
                    TextToSend.Focus();
                    focusTextbox = false;
                }
            }
        }

        private void OnlineThumbButton_Click(object sender, EventArgs e)
        {
            SetStatus(ToxUserStatus.NONE);
        }

        private void AwayThumbButton_Click(object sender, EventArgs e)
        {
            SetStatus(ToxUserStatus.AWAY);
        }

        private void BusyThumbButton_Click(object sender, EventArgs e)
        {
            SetStatus(ToxUserStatus.BUSY);
        }

        private void ListViewTabControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RequestsTabItem.IsSelected)
                RequestsTabItem.Header = "Requests";
        }

        private void StatusRectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            StatusContextMenu.PlacementTarget = this;
            StatusContextMenu.IsOpen = true;
        }

        private void MenuItem_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            MenuItem menuItem = (MenuItem)e.Source;
            SetStatus((ToxUserStatus)int.Parse(menuItem.Tag.ToString()));
        }

        private void SetStatus(ToxUserStatus? newStatus)
        {
            if (newStatus == null)
                newStatus = tox.GetSelfUserStatus();
            else
                tox.SetUserStatus(newStatus.GetValueOrDefault());

            switch (newStatus)
            {
                case ToxUserStatus.NONE:
                    StatusRectangle.Fill = new SolidColorBrush(Color.FromRgb(6, 225, 1));
                    break;

                case ToxUserStatus.BUSY:
                    StatusRectangle.Fill = new SolidColorBrush(Color.FromRgb(214, 43, 79));
                    break;

                case ToxUserStatus.AWAY:
                    StatusRectangle.Fill = new SolidColorBrush(Color.FromRgb(229, 222, 31));
                    break;

                case ToxUserStatus.INVALID:
                    StatusRectangle.Fill = new SolidColorBrush(Colors.Red);
                    break;
            }
        }

        private void CallButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (call != null)
                return;

            if (tox.GetFriendConnectionStatus(current_number) != 1)
                return;

            int call_index;
            ToxAvError error = toxav.Call(current_number, ToxAvCallType.Audio, 30, out call_index);
            if (error != ToxAvError.None)
                return;

            int friendnumber = toxav.GetPeerID(call_index, 0);
            call = new ToxCall(tox, toxav, call_index, friendnumber);

            CallButton.Visibility = Visibility.Hidden;
            HangupButton.Visibility = Visibility.Visible;
            AddCallControl(friendnumber, "Calling {0}...");
        }

        private void MainHangupButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (call == null)
                return;

            EndCall();
        }

        private void FileButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (current_type != typeof(FriendControl))
                return;

            if (tox.GetFriendConnectionStatus(current_number) != 1)
                return;

            OpenFileDialog dialog = new OpenFileDialog();
            dialog.InitialDirectory = Environment.CurrentDirectory;
            dialog.Multiselect = false;

            if (dialog.ShowDialog() != true)
                return;

            string filename = dialog.FileName;
            FileInfo info = new FileInfo(filename);
            int filenumber = tox.NewFileSender(current_number, (ulong)info.Length, filename.Split('\\').Last<string>());

            if (filenumber == -1)
                return;

            FileTransfer ft = convdic[current_number].AddNewFileTransfer(tox, current_number, filenumber, filename, (ulong)info.Length, true);
            ft.Control.SetStatus(string.Format("Waiting for {0} to accept...", tox.GetName(current_number)));
            ft.Control.AcceptButton.Visibility = Visibility.Collapsed;
            ft.Control.DeclineButton.Visibility = Visibility.Visible;

            ft.Control.OnDecline += delegate(int friendnum, int filenum)
            {
                if (ft.Thread != null)
                {
                    ft.Thread.Abort();
                    ft.Thread.Join();
                }

                if (ft.Stream != null)
                    ft.Stream.Close();

                if (!ft.IsSender)
                    tox.FileSendControl(ft.FriendNumber, 1, filenumber, ToxFileControl.KILL, new byte[0]);
                else
                    tox.FileSendControl(ft.FriendNumber, 0, filenumber, ToxFileControl.KILL, new byte[0]);
            };

            transfers.Add(ft);
        }
    }
}
