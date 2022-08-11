using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    internal struct InputPacketHeader
    {
        public ushort StateA;
        public ushort StateB;
        public float LerpMsec;
    }

    /// <summary>
    /// Client entity manager
    /// </summary>
    public sealed class ClientEntityManager : EntityManager
    {
        /// <summary>
        /// Current interpolated server tick
        /// </summary>
        public ushort ServerTick { get; private set; }

        public ushort RawServerTick => _stateA != null ? _stateA.Tick : (ushort)0;

        public ushort RawTargetServerTick => _stateB != null ? _stateB.Tick : RawServerTick;
        
        /// <summary>
        /// Stored input commands count for prediction correction
        /// </summary>
        public int StoredCommands => _inputCommands.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public ushort LastProcessedTick => _stateA?.ProcessedTick ?? 0;

        public ushort LastReceivedTick => _stateA?.LastReceivedTick ?? 0;
        
        /// <summary>
        /// States count in interpolation buffer
        /// </summary>
        public int LerpBufferCount => _lerpBuffer.Count;

        private const int InterpolateBufferSize = 10;
        private const int InputBufferSize = 128;
        private static readonly int InputHeaderSize = Unsafe.SizeOf<InputPacketHeader>();
        
        private readonly NetPeer _localPeer;
        private readonly SortedList<ushort, ServerStateData> _receivedStates = new SortedList<ushort, ServerStateData>(new SequenceComparer());
        private readonly Queue<ServerStateData> _statesPool = new Queue<ServerStateData>(MaxSavedStateDiff);
        private readonly NetDataReader _inputReader = new NetDataReader();
        private readonly Queue<NetDataWriter> _inputCommands = new Queue<NetDataWriter>(InputBufferSize);
        private readonly Queue<NetDataWriter> _inputPool = new Queue<NetDataWriter>(InputBufferSize);
        private readonly SortedSet<ServerStateData> _lerpBuffer = new SortedSet<ServerStateData>(new ServerStateComparer());
        private readonly Queue<(ushort, EntityLogic)> _spawnPredictedEntities = new Queue<(ushort, EntityLogic)>();
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly byte[][] _predictedEntities = new byte[MaxSyncedEntityCount][];
        private readonly byte[] _tempData = new byte[MaxFieldSize];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;

        private struct SyncCallInfo
        {
            public MethodCallDelegate OnSync;
            public InternalEntity Entity;
            public int PrevDataPos;
        }
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        
        private ushort _remoteCallsTick;
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;

        //adaptive lerp vars
        private float _adaptiveMiddlePoint = 3f;
        private readonly float[] _jitterSamples = new float[10];
        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new Stopwatch();

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="localPeer">Local NetPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ClientEntityManager(EntityTypesMap typesMap, NetPeer localPeer, byte headerByte, byte framesPerSecond) : base(typesMap, NetworkMode.Client, framesPerSecond)
        {
            _localPeer = localPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = PacketClientSync;
            AliveEntities.OnAdded += InitInterpolation;
        }

        /// <summary>
        /// Read incoming data in case of first byte is == headerByte
        /// </summary>
        /// <param name="reader">Reader with data (will be recycled inside, also works with autorecycle)</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPacketReader reader)
        {
            if (reader.PeekByte() == _sendBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize(reader);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Read incoming data omitting header byte
        /// </summary>
        /// <param name="reader"></param>
        public unsafe void Deserialize(NetPacketReader reader)
        {
            byte packetType = reader.GetByte();
            if(packetType == PacketBaselineSync)
            {
                _stateB = null;
                _stateA = new ServerStateData
                {
                    IsBaseline = true,
                    Size = reader.GetInt()
                };
                InternalPlayerId = reader.GetByte();
                Logger.Log($"[CEM] Got baseline sync. Assigned player id: {InternalPlayerId}");
                Utils.ResizeOrCreate(ref _stateA.Data, _stateA.Size);
                
                int decodedBytes = LZ4Codec.Decode(
                    reader.RawData,
                    reader.Position,
                    reader.AvailableBytes,
                    _stateA.Data,
                    0,
                    _stateA.Size);
                if (decodedBytes != _stateA.Size)
                {
                    Logger.LogError("Error on decompress");
                    return;
                }
                _stateA.Tick = BitConverter.ToUInt16(_stateA.Data, 0);
                _stateA.Offset = 2;
                _inputCommands.Clear();
                int bytesRead = _stateA.Offset;
                fixed (byte* readerData = _stateA.Data)
                {
                    while (bytesRead < _stateA.Size)
                    {
                        ushort entityId = BitConverter.ToUInt16(_stateA.Data, bytesRead);
                        bytesRead += 2;
                        ReadEntityStateFullSync(readerData, ref bytesRead, entityId);
                        if (bytesRead == -1)
                            return;
                    }
                    //Make OnSyncCalls
                    for (int i = 0; i < _syncCallsCount; i++)
                    {
                        ref var syncCall = ref _syncCalls[i];
                        syncCall.OnSync(syncCall.Entity, readerData + syncCall.PrevDataPos, 1); //TODO: count!!!
                    }
                    _syncCallsCount = 0;
                }
                _remoteCallsTick = _stateA.Tick;
                _isSyncReceived = true;
                _jitterTimer.Restart();
            }
            else
            {
                bool isLastPart = packetType == PacketDiffSyncLast;
                ushort newServerTick = reader.GetUShort();
                if (Utils.SequenceDiff(newServerTick, _stateA.Tick) <= 0)
                {
                    reader.Recycle();
                    return;
                }
                
                //sample jitter
                _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
                _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                //reset timer
                _jitterTimer.Reset();
                
                if(!_receivedStates.TryGetValue(newServerTick, out var serverState))
                {
                    if (_receivedStates.Count > MaxSavedStateDiff)
                    {
                        Logger.LogWarning("[CEM] Too much states received: this should be rare thing");
                        var minimal = _receivedStates.Keys[0];
                        if (Utils.SequenceDiff(newServerTick, minimal) > 0)
                        {
                            serverState = _receivedStates[minimal];
                            _receivedStates.Remove(minimal);
                            serverState.Reset(newServerTick);
                        }
                        else
                        {
                            reader.Recycle();
                            return;
                        }
                    }
                    else if (_statesPool.Count > 0)
                    {
                        serverState = _statesPool.Dequeue();
                        serverState.Reset(newServerTick);
                    }
                    else
                    {
                        serverState = new ServerStateData { Tick = newServerTick };
                    }
                    _receivedStates.Add(newServerTick, serverState);
                }
                
                //if got full state - add to lerp buffer
                if(serverState.ReadPart(isLastPart, reader))
                {
                    if (Utils.SequenceDiff(serverState.LastReceivedTick, _lastReceivedInputTick) > 0)
                        _lastReceivedInputTick = serverState.LastReceivedTick;
                    
                    _receivedStates.Remove(serverState.Tick);
                    
                    if (_lerpBuffer.Count >= InterpolateBufferSize)
                    {
                        if (Utils.SequenceDiff(serverState.Tick, _lerpBuffer.Min.Tick) > 0)
                        {
                            _timer = _lerpTime;
                            if (_stateB != null || PreloadNextState())
                            {
                                GoToNextState();
                            }
                            _lerpBuffer.Add(serverState);
                        }
                        else
                        {
                            _statesPool.Enqueue(serverState);
                        }
                    }
                    else
                    {
                        _lerpBuffer.Add(serverState);
                    }
                }
            }
        }

        private bool PreloadNextState()
        {
            if (_lerpBuffer.Count == 0)
            {
                if (_adaptiveMiddlePoint < 3f)
                    _adaptiveMiddlePoint = 3f;
                return false;
            }

            float jitterSum = 0f;
            bool adaptiveIncreased = false;
            for (int i = 0; i < _jitterSamples.Length - 1; i++)
            {
                float jitter = Math.Abs(_jitterSamples[i] - _jitterSamples[i + 1]) * FramesPerSecond;
                jitterSum += jitter;
                if (jitter > _adaptiveMiddlePoint)
                {
                    _adaptiveMiddlePoint = jitter;
                    adaptiveIncreased = true;
                }
            }

            if (!adaptiveIncreased)
            {
                jitterSum /= _jitterSamples.Length;
                _adaptiveMiddlePoint = Utils.Lerp(_adaptiveMiddlePoint, Math.Max(1f, jitterSum), 0.05f);
            }

            _stateB = _lerpBuffer.Min;
            _lerpBuffer.Remove(_stateB);
            _lerpTime = 
                Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF *
                (1f - (_lerpBuffer.Count - _adaptiveMiddlePoint) * 0.02f);
            _stateB.Preload(EntitiesDict);

            //remove processed inputs
            while (_inputCommands.Count > 0)
            {
                if (Utils.SequenceDiff(_stateB.ProcessedTick, (ushort)(Tick - _inputCommands.Count + 1)) >= 0)
                {
                    var inputWriter = _inputCommands.Dequeue();
                    inputWriter.Reset();
                    _inputPool.Enqueue(inputWriter);
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        private unsafe void GoToNextState()
        {
            _statesPool.Enqueue(_stateA);
            _stateA = _stateB;
            _stateB = null;
            
            fixed (byte* readerData = _stateA.Data)
            {
                for (int i = 0; i < _stateA.PreloadDataCount; i++)
                {
                    ref var preloadData = ref _stateA.PreloadDataArray[i];
                    int offset = preloadData.DataOffset;
                    if (preloadData.EntityFieldsOffset == -1)
                        ReadEntityStateFullSync(readerData, ref offset, preloadData.EntityId);
                    else
                        ReadEntityData(EntitiesDict[preloadData.EntityId], readerData, ref offset, false);
                    if (offset == -1)
                        break;
                }
                //Make OnSyncCalls
                for (int i = 0; i < _syncCallsCount; i++)
                {
                    ref var syncCall = ref _syncCalls[i];
                    syncCall.OnSync(syncCall.Entity, readerData + syncCall.PrevDataPos, 1); //TODO: count!!!
                }
                _syncCallsCount = 0;
            }

            _timer -= _lerpTime;
            
            //reset owned entities
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                
                var localEntity = entity;
                fixed (byte* latestEntityData = _predictedEntities[entity.Id])
                {
                    ref var classData = ref entity.GetClassData();
                    byte* entityPtr = Utils.GetPtr(ref localEntity);
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        if (field.Flags.HasFlagFast(SyncFlags.OnlyForRemote))
                            continue;
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            ref var syncableField = ref Unsafe.AsRef<SyncableField>(entityPtr + field.Offset);
                            byte* syncVarPtr = Utils.GetPtr(ref syncableField) + field.SyncableSyncVarOffset;
                            Unsafe.CopyBlock(syncVarPtr, latestEntityData + field.FixedOffset, field.Size);
                        }
                        else //value or entity
                        {
                            Unsafe.CopyBlock(entityPtr + field.Offset, latestEntityData + field.FixedOffset, field.Size);
                        }
                    }
                }
            }
            
            //reapply input
            UpdateMode = UpdateMode.PredictionRollback;
            foreach (var inputCommand in _inputCommands)
            {
                //reapply input data
                _inputReader.SetSource(inputCommand.Data, InputHeaderSize, inputCommand.Length);
                foreach(var controller in GetControllers<HumanControllerLogic>())
                {
                    controller.ReadInput(_inputReader);
                }
                foreach (var entity in AliveEntities)
                {
                    if(entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
            }
            UpdateMode = UpdateMode.Normal;
            
            //update interpolated position
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                ref var classData = ref entity.GetClassData();
                var localEntity = entity;
                byte* entityPtr = Utils.GetPtr(ref localEntity);
                
                for(int i = 0; i < classData.InterpolatedCount; i++)
                {
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id])
                        classData.Fields[i].GetToFixedOffset(entityPtr, currentDataPtr);
                }
            }
            
            //delete predicted
            while (_spawnPredictedEntities.TryPeek(out var info))
            {
                if (Utils.SequenceDiff(_stateA.ProcessedTick, info.Item1) >= 0)
                {
                    _spawnPredictedEntities.Dequeue();
                    info.Item2.DestroyInternal();
                }
                else
                {
                    break;
                }
            }
            
            //load next state
            double prevLerpTime = _lerpTime;
            if (PreloadNextState())
            {
                //adjust lerp timer
                _timer *= (prevLerpTime / _lerpTime);
            }
        }

        public override unsafe void Update()
        {
            if (!_isSyncReceived)
                return;
            
            //logic update
            ushort prevTick = Tick;
            base.Update();
            
            if (_stateB != null || PreloadNextState())
            {
                _timer += VisualDeltaTime;
                if (_timer >= _lerpTime)
                {
                    GoToNextState();
                }
            }

            if (_stateB != null)
            {
                //remote interpolation
                float fTimer = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesDict[preloadData.EntityId];
                    var fields = entity.GetClassData().Fields;
                    byte* entityPtr = Utils.GetPtr(ref entity);
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = _stateB.Data)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            var field = fields[interpolatedCache.Field];
                            field.Interpolator(
                                initialDataPtr + field.FixedOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset,
                                entityPtr + field.Offset,
                                fTimer);
                        }
                    }
                }
            }

            //local interpolation
            float localLerpT = LerpFactor;
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;
                
                var entityLocal = entity;
                ref var classData = ref entity.GetClassData();
                byte* entityPtr = Utils.GetPtr(ref entityLocal);
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                       prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    for(int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        var field = classData.Fields[i];
                        field.Interpolator(
                            prevDataPtr + field.FixedOffset,
                            currentDataPtr + field.FixedOffset,
                            entityPtr + field.Offset,
                            localLerpT);
                    }
                }
            }

            //send buffered input
            if (Tick != prevTick)
            {
                //pack tick first
                int offset = 4;
                fixed (byte* sendBuffer = _sendBuffer)
                {
                    ushort currentTick = (ushort)(Tick - _inputCommands.Count + 1);
                    ushort tickIndex = 0;
                    
                    foreach (var inputCommand in _inputCommands)
                    {
                        if (Utils.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
                        {
                            currentTick++;
                            continue;
                        }
                        
                        fixed (byte* inputData = inputCommand.Data)
                        {
                            if (offset + inputCommand.Length + sizeof(ushort) > _localPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
                            {
                                Unsafe.Write(sendBuffer + 2, currentTick);
                                _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                                offset = 4;
                                
                                currentTick += tickIndex;
                                tickIndex = 0;
                            }
                            
                            //put size
                            Unsafe.Write(sendBuffer + offset, (ushort)(inputCommand.Length - InputHeaderSize));
                            offset += sizeof(ushort);
                            
                            //put data
                            Unsafe.CopyBlock(sendBuffer + offset, inputData, (uint)inputCommand.Length);
                            offset += inputCommand.Length;
                        }

                        tickIndex++;
                        if (tickIndex == MaxSavedStateDiff)
                        {
                            break;
                        }
                    }
                    Unsafe.Write(sendBuffer + 2, currentTick);
                    _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                    _localPeer.NetManager.TriggerUpdate();
                }
            }
            
            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                entity.VisualUpdate();
            }
        }

        internal void AddOwned(EntityLogic entity)
        {
            if(entity.GetClassData().IsUpdateable && !entity.GetClassData().UpdateOnClient)
                AliveEntities.Add(entity);
        }
        
        internal void RemoveOwned(EntityLogic entity)
        {
            AliveEntities.Remove(entity);
        }

        private unsafe void InitInterpolation(InternalEntity entity)
        {
            ref var classData = ref ClassDataDict[entity.ClassId];
            byte* entityPtr = Utils.GetPtr(ref entity);
            
            if (classData.InterpolatedFieldsSize > 0)
            {
                Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
                Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], classData.InterpolatedFieldsSize);
            }
            
            if (!entity.IsLocal)
            {
                ref byte[] predictedData = ref _predictedEntities[entity.Id];
                Utils.ResizeOrCreate(ref predictedData, classData.FixedFieldsSize);
                
                fixed (byte* predictedPtr = predictedData, interpDataPtr = _interpolatedInitialData[entity.Id])
                {
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        var field = classData.Fields[i];
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            ref var syncableField = ref Unsafe.AsRef<SyncableField>(entityPtr + field.Offset);
                            byte* syncVarPtr = Utils.GetPtr(ref syncableField) + field.SyncableSyncVarOffset;
                            Unsafe.CopyBlock(predictedPtr + field.FixedOffset, syncVarPtr, field.Size);
                        }
                        else //value or entity
                        {
                            Unsafe.CopyBlock(predictedPtr + field.FixedOffset, entityPtr + field.Offset, field.Size);
                        }
                        if (field.Interpolator != null)
                        {
                            Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, entityPtr + field.Offset, field.Size);
                        }
                    }
                }
            }
            else
            {
                fixed (byte* interpDataPtr = _interpolatedInitialData[entity.Id])
                {
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        var field = classData.Fields[i];
                        if (field.Interpolator != null)
                        {
                            Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, entityPtr + field.Offset, field.Size);
                        }
                    }
                }
            }
        }

        internal void AddPredictedInfo(EntityLogic e)
        {
            _spawnPredictedEntities.Enqueue((Tick, e));
        }

        protected override unsafe void OnLogicTick()
        {
            if (_stateB != null)
            {
                _logicLerpMsec = (float)(_timer / _lerpTime);
                ServerTick = (ushort)(_stateA.Tick + Math.Round(Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * _logicLerpMsec));
                fixed (byte* rawData = _stateB.Data)
                {
                    int maxTick = -1;
                    for (int i = 0; i < _stateB.RemoteCallsCount; i++)
                    {
                        ref var rpcCache = ref _stateB.RemoteCallsCaches[i];
                        if (Utils.SequenceDiff(rpcCache.Tick, _remoteCallsTick) > 0 && Utils.SequenceDiff(rpcCache.Tick, ServerTick) <= 0)
                        {
                            if (maxTick == -1 || Utils.SequenceDiff(rpcCache.Tick, (ushort)maxTick) > 0)
                            {
                                maxTick = rpcCache.Tick;
                            }
                            var entity = EntitiesDict[rpcCache.EntityId];
                            if (rpcCache.FieldId == byte.MaxValue)
                            {
                                rpcCache.Delegate(entity, rawData + rpcCache.Offset, rpcCache.Count);
                            }
                            else
                            {
                                var fieldPtr = Utils.GetPtr(ref entity) + ClassDataDict[entity.ClassId].SyncableFields[rpcCache.FieldId].Offset;
                                rpcCache.Delegate(Unsafe.AsRef<SyncVar>(fieldPtr), rawData + rpcCache.Offset, rpcCache.Count);
                            }
                        }
                    }
                    if(maxTick != -1)
                        _remoteCallsTick = (ushort)maxTick;
                }        
            }

            if (_inputCommands.Count > InputBufferSize)
            {
                _inputCommands.Clear();
            }
            var inputWriter = _inputPool.Count > 0 ? _inputPool.Dequeue() : new NetDataWriter(true, InputHeaderSize);
            var inputPacketHeader = new InputPacketHeader
            {
                StateA   = _stateA.Tick,
                StateB   = _stateB?.Tick ?? _stateA.Tick,
                LerpMsec = _logicLerpMsec
            };
            fixed(void* writerData = inputWriter.Data)
                Unsafe.Copy(writerData, ref inputPacketHeader);
            inputWriter.SetPosition(InputHeaderSize);
            
            //generate inputs
            foreach(var controller in GetControllers<HumanControllerLogic>())
            {
                controller.GenerateInput(inputWriter);
                if (inputWriter.Length > NetConstants.MaxUnreliableDataSize - 2)
                {
                    Logger.LogError($"Input too large: {inputWriter.Length-InputHeaderSize} bytes");
                    break;
                }
            }
            
            //read
            _inputReader.SetSource(inputWriter.Data, InputHeaderSize, inputWriter.Length);
            foreach (var controller in GetControllers<HumanControllerLogic>())
            {
                controller.ReadInput(_inputReader);
            }
            _inputCommands.Enqueue(inputWriter);

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    ref var classData = ref ClassDataDict[entity.ClassId];
                    var entityLocal = entity;
                    byte* entityPtr = Utils.GetPtr(ref entityLocal);
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        //restore previous
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.SetFromFixedOffset(entityPtr, currentDataPtr);
                        }

                        //update
                        entity.Update();
                
                        //save current
                        Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.GetToFixedOffset(entityPtr, currentDataPtr);
                        }
                    }
                }
                else
                {
                    entity.Update();
                }
            }
        }
        
        private unsafe void ReadEntityStateFullSync(byte* rawData, ref int readerPosition, ushort entityInstanceId)
        {
            if (entityInstanceId == InvalidEntityId || entityInstanceId >= MaxSyncedEntityCount)
            {
                Logger.LogError($"Bad data (id > MaxEntityCount) {entityInstanceId} >= {MaxSyncedEntityCount}");
                readerPosition = -1;
                return;
            }
            var entity = EntitiesDict[entityInstanceId];
            byte version = rawData[readerPosition];
            ushort classId = Unsafe.Read<ushort>(rawData + readerPosition + 1);
            readerPosition += 3;

            //remove old entity
            if (entity != null && entity.Version != version)
            {
                //this can be only on logics (not on singletons)
                Logger.Log($"[CEM] Replace entity by new: {version}");
                var entityLogic = (EntityLogic) entity;
                if(!entityLogic.IsDestroyed)
                    entityLogic.DestroyInternal();
                entity = null;
            }
            
            //create new
            if(entity == null)
            {
                int localReaderPosition = readerPosition;
                AddEntity<InternalEntity>(
                    new EntityParams(classId, entityInstanceId, version, this),
                    internalEntity => ReadEntityData(internalEntity, rawData, ref localReaderPosition, true));
                readerPosition = localReaderPosition;
                //Logger.Log($"[CEM] Add entity: {entity.GetType()}");
            }
            else
            {
                ReadEntityData(entity, rawData, ref readerPosition, true);
            }
        }

        private unsafe void ReadEntityData(InternalEntity entity, byte* rawData, ref int readerPosition, bool fullSync)
        {
            ref var classData = ref ClassDataDict[entity.ClassId];

            //create interpolation buffers
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            Utils.ResizeOrCreate(ref interpolatedInitialData, classData.InterpolatedFieldsSize);
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);

            byte* entityPtr = Utils.GetPtr(ref entity);
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            bool writeInterpolationData = entity.IsServerControlled || fullSync;
            
            entity.OnSyncStart();
            fixed (byte* interpDataPtr = interpolatedInitialData, tempData = _tempData, latestEntityData = _predictedEntities[entity.Id])
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                        continue;
                    
                    ref var field = ref classData.Fields[i];
                    byte* fieldPtr = entityPtr + field.Offset;
                    byte* readDataPtr = rawData + readerPosition;
                    
                    if (field.FieldType == FieldType.SyncableSyncVar)
                    {
                        ref var syncableField = ref Unsafe.AsRef<SyncableField>(fieldPtr);
                        byte* syncVarPtr = Utils.GetPtr(ref syncableField) + field.SyncableSyncVarOffset;
                        Unsafe.CopyBlock(syncVarPtr, readDataPtr, field.Size);
                        if(latestEntityData != null && entity.IsLocalControlled)
                            Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                    }
                    else
                    {
                        if (field.Interpolator != null && writeInterpolationData)
                        {
                            //this is interpolated save for future
                            Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, readDataPtr, field.Size);
                        }

                        if (field.OnSync != null)
                        {
                            Unsafe.CopyBlock(tempData, fieldPtr, field.Size);
                            Unsafe.CopyBlock(fieldPtr, readDataPtr, field.Size);
                            if(latestEntityData != null && entity.IsLocalControlled)
                                Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                            //put prev data into reader for SyncCalls
                            Unsafe.CopyBlock(readDataPtr, tempData, field.Size);
                            if (Utils.memcmp(readDataPtr, fieldPtr, field.PtrSize) != 0)
                            {
                                _syncCalls[_syncCallsCount++] = new SyncCallInfo
                                {
                                    OnSync = field.OnSync,
                                    Entity = entity,
                                    PrevDataPos = readerPosition
                                };
                            }
                        }
                        else
                        {
                            Unsafe.CopyBlock(fieldPtr, readDataPtr, field.Size);
                            if(latestEntityData != null && entity.IsLocalControlled)
                                Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                        }
                    }
                    readerPosition += field.IntSize;
                }
            }
            
            if (fullSync)
                for (int i = 0; i < classData.SyncableFields.Length; i++)
                    Unsafe.AsRef<SyncableField>(entityPtr + classData.SyncableFields[i].Offset).FullSyncRead(rawData, ref readerPosition);
            
            entity.OnSyncEnd();
        }
    }
}