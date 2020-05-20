﻿using Microsoft.Extensions.Configuration;
using org.apache.zookeeper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNet.Extensions.Configuration.Zookeeper
{
  /// <summary>
  /// A zookeeper based <see cref="ConfigurationProvider"/>.
  /// </summary>
  internal class ZookeeperConfigurationProvider : ConfigurationProvider
  {
    private readonly ZookeeperOption _option;
    private readonly IZooKeeperFactory _zooKeeperFactory;
    private readonly ManualResetEvent _connectedEvent;
    private AutoResetEvent _loadCompletedEvent;
    private ZooKeeper _zooKeeper;
    private NodeWatcher _watcher;
    private PathTree _pathTree;
    private Exception _loadException;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="option">the zookeeper option.</param>
    /// <param name="zookeeperFactory">the zookeeper factory.</param>
    public ZookeeperConfigurationProvider(ZookeeperOption option, IZooKeeperFactory zookeeperFactory)
    {
      _option = option;
      _zooKeeperFactory = zookeeperFactory;
      _zooKeeper = _zooKeeperFactory.CreateZooKeeper(_option.ConnectionString, _option.SessionTimeout, _option.AuthInfo, out _watcher);
      _watcher.StateChanged += OnStateChanged;
      _watcher.NodeChanged += OnNodeChanged;
      _connectedEvent = new ManualResetEvent(false);
      _loadCompletedEvent = new AutoResetEvent(false);
    }

    /// <summary>
    /// Loads the configuration data from zookeeper.
    /// </summary>
    public override void Load()
    {
      var isConnected = _connectedEvent.WaitOne(_option.ConnectionTimeout);
      if (!isConnected)
      {
        throw new Exception("connect to zookeeper timeout");
      }
      _loadCompletedEvent.WaitOne();
      if (_loadException != null)
      {
        throw _loadException;
      }
    }

    private async Task LoadAsync()
    {
      var data = new Dictionary<string, string>();
      var stack = new Stack<KeyValuePair<string, PathTree.TreeNode>>();
      _pathTree = new PathTree();
      stack.Push(new KeyValuePair<string, PathTree.TreeNode>(_option.RootPath ?? "/", _pathTree.Root));

      while (stack.Count > 0)
      {
        var pair = stack.Pop();
        var path = pair.Key;
        var currentNode = pair.Value;
        string value = null;
        try
        { value = await GetDataAsync(path, true).ConfigureAwait(false); }
        catch (KeeperException.NoAuthException)
        { /*There isn't valid authentication for this key, so it is not read*/ }

        if (value != null)
        {
          var key = ConvertPathToKey(path);
          data[key] = value;

          var children = await GetChildrenAsync(path, true);
          children?.ForEach(item =>
          {
            var node = _pathTree.AddNode(item, currentNode);
            stack.Push(new KeyValuePair<string, PathTree.TreeNode>(path + "/" + item, node));
          });
        }
      }

      data.Remove("");
      Data = data;
    }

    private string ConvertPathToKey(string path)
    {
      if (string.IsNullOrEmpty(path))
      {
        throw new ArgumentNullException(nameof(path));
      }
      return path.Substring(_option.RootPath.Length).Trim('/').Replace("/", ConfigurationPath.KeyDelimiter);
    }

    private async Task<string> GetDataAsync(string path, bool watch = false)
    {
      var result = await _zooKeeper.getDataAsync(path, watch);
      if (result == null) return null;
      return result.Data == null ? string.Empty : Encoding.UTF8.GetString(result.Data);
    }

    private async Task<List<string>> GetChildrenAsync(string path, bool watch = false)
    {
      var childResult = await _zooKeeper.getChildrenAsync(path, watch);
      if (childResult == null) return null;
      return childResult.Children;
    }

    private async Task OnStateChanged(WatchedEvent arg)
    {

      switch (arg.getState())
      {
        case Watcher.Event.KeeperState.Disconnected:
          _connectedEvent.Reset();
          break;
        case Watcher.Event.KeeperState.SyncConnected:
          _connectedEvent.Set();
          try
          {
            await LoadAsync();
          }
          catch (Exception ex)
          {
            HandleException(ex);
          }
          finally
          {
            _loadCompletedEvent.Set();
          }

          break;
        case Watcher.Event.KeeperState.AuthFailed:
          //todo:throw custom exception.
          throw new Exception("connect to zookeeper auth failed");
        case Watcher.Event.KeeperState.ConnectedReadOnly:
          //we won't connect readonly when instantiate zookeeper.
          break;
        case Watcher.Event.KeeperState.Expired:

          _connectedEvent.Reset();
          _loadCompletedEvent.Set();
          _zooKeeper = _zooKeeperFactory.CreateZooKeeper(_option.ConnectionString, _option.SessionTimeout, _option.AuthInfo, out _watcher);
          _watcher.StateChanged += OnStateChanged;
          _watcher.NodeChanged += OnNodeChanged;
          break;
        default:
          break;
      }
    }

    private async Task OnNodeChanged(WatchedEvent arg)
    {
      var type = arg.get_Type();
      var path = arg.getPath();
      var key = ConvertPathToKey(path);
      switch (type)
      {
        case Watcher.Event.EventType.NodeDeleted:
          await OnNodeDeleted(path, key);
          OnReload();
          break;
        case Watcher.Event.EventType.NodeDataChanged:
          await OnNodeDataChanged(path, key);
          OnReload();
          break;
        case Watcher.Event.EventType.NodeChildrenChanged:
          await OnNodeChildrenChanged(path, key);
          OnReload();
          break;
        default:
          break;
      }
    }

    private Task OnNodeDeleted(string path, string key)
    {
      Data.Remove(key);
      _pathTree.RemoveNode(path);
      return Task.CompletedTask;
    }

    private async Task OnNodeDataChanged(string path, string key)
    {
      var value = await GetDataAsync(path, true);
      if (value != null)
      {
        Data[key] = value;
      }
    }

    private async Task OnNodeChildrenChanged(string path, string key)
    {
      var zooKeeperKeys = await GetChildrenAsync(path, true);
      if (zooKeeperKeys == null) return;

      var node = _pathTree.FindNode(path.Equals(_option.RootPath) ? "/" : path.Substring(_option.RootPath.Length));
      var originalKeys = node.Children.Select(item => item.Key).ToList();

      //indicate that it was added in zooKeeper.
      zooKeeperKeys.Except(originalKeys).ToList().ForEach(async childKey =>
      {
        var pathToAdd = path + "/" + childKey;
        var value = await GetDataAsync(pathToAdd, true);
        if (value != null)
        {
          Data[ConvertPathToKey(pathToAdd)] = value;
          _pathTree.AddNode(childKey, node);
        }
      });

      //indicate that it was removed in zooKeeper.
      originalKeys.Except(zooKeeperKeys).ToList().ForEach(childKey =>
      {
        var pathToRemove = path + "/" + childKey;
        Data.Remove(ConvertPathToKey(pathToRemove));

        node.Children.RemoveAll(item => item.Key == childKey);
        _pathTree.RemoveNode(pathToRemove.Substring(_option.RootPath.Length));
      });
    }

    private void HandleException(Exception ex)
    {
      _loadException = ex;
    }
  }
}