using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Optimized memory manager for stable CPU and memory usage during continuous audio recording
/// </summary>
public sealed class OptimizedMemoryManager : IDisposable
{
    private readonly ILogger<OptimizedMemoryManager> _logger;
    private readonly RecordingConfig _config;
    
    // Memory pools for different buffer sizes
    private readonly ArrayPool<byte> _bytePool;
    private readonly ArrayPool<float> _floatPool;
    private readonly ArrayPool<short> _shortPool;
    
    // Custom pools for common audio buffer sizes
    private readonly ConcurrentQueue<byte[]> _audioBufferPool = new();
    private readonly ConcurrentQueue<float[]> _processingBufferPool = new();
    
    // Memory usage tracking
    private long _totalAllocatedBytes;
    private long _poolAllocatedBytes;
    private long _peakMemoryUsage;
    private DateTime _lastGcTime = DateTime.UtcNow;
    
    // GC optimization
    private readonly Timer? _gcOptimizationTimer;
    private readonly object _gcLock = new();
    
    // Buffer size constants for audio processing
    private const int StandardAudioBufferSize = 4096;    // ~85ms at 48kHz
    private const int LargeAudioBufferSize = 8192;       // ~170ms at 48kHz
    private const int ProcessingBufferSize = 2048;       // Processing frame size
    
    public OptimizedMemoryManager(ILogger<OptimizedMemoryManager> logger, RecordingConfig config)
    {
        _logger = logger;
        _config = config;
        
        // Initialize array pools with optimized sizing
        _bytePool = ArrayPool<byte>.Create(maxArrayLength: LargeAudioBufferSize * 4, maxArraysPerBucket: 50);
        _floatPool = ArrayPool<float>.Create(maxArrayLength: LargeAudioBufferSize, maxArraysPerBucket: 50);
        _shortPool = ArrayPool<short>.Create(maxArrayLength: LargeAudioBufferSize, maxArraysPerBucket: 50);
        
        // Pre-populate custom pools
        PrePopulatePools();
        
        // Setup GC optimization timer if enabled
        if (config.EnableGCOptimizations)
        {
            _gcOptimizationTimer = new Timer(OptimizeGarbageCollection, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        
        _logger.LogInformation("Optimized memory manager initialized with {maxPoolSize}MB limit", config.MaxMemoryPoolSizeMB);
    }
    
    /// <summary>
    /// Pre-populate memory pools with commonly used buffer sizes
    /// </summary>
    private void PrePopulatePools()
    {
        var maxPoolSizeBytes = _config.MaxMemoryPoolSizeMB * 1024 * 1024;
        var bufferCount = Math.Min(100, maxPoolSizeBytes / (StandardAudioBufferSize * 8)); // Conservative estimate
        
        // Pre-allocate audio buffers
        for (int i = 0; i < bufferCount / 4; i++)
        {
            _audioBufferPool.Enqueue(new byte[StandardAudioBufferSize]);
            _audioBufferPool.Enqueue(new byte[LargeAudioBufferSize]);
            _processingBufferPool.Enqueue(new float[ProcessingBufferSize]);
            _processingBufferPool.Enqueue(new float[StandardAudioBufferSize / 2]); // Typical float buffer size
        }
        
        _poolAllocatedBytes = _audioBufferPool.Count * (StandardAudioBufferSize + LargeAudioBufferSize) + 
                             _processingBufferPool.Count * ProcessingBufferSize * sizeof(float);
        
        _logger.LogDebug("Pre-populated memory pools: {audioBuffers} audio buffers, {processingBuffers} processing buffers", 
            _audioBufferPool.Count, _processingBufferPool.Count);
    }
    
    /// <summary>
    /// Rent a byte buffer for audio data
    /// </summary>
    public byte[] RentAudioBuffer(int minimumSize)
    {
        // Try custom pool first for standard sizes
        if (minimumSize <= StandardAudioBufferSize && _audioBufferPool.TryDequeue(out var pooledBuffer))
        {
            return pooledBuffer;
        }
        
        // Fall back to array pool
        var buffer = _bytePool.Rent(minimumSize);
        Interlocked.Add(ref _totalAllocatedBytes, buffer.Length);
        UpdatePeakMemoryUsage();
        
        return buffer;
    }
    
    /// <summary>
    /// Return a byte buffer to the pool
    /// </summary>
    public void ReturnAudioBuffer(byte[] buffer)
    {
        if (buffer == null) return;
        
        // Return to custom pool if it's a standard size and pool isn't full
        if ((buffer.Length == StandardAudioBufferSize || buffer.Length == LargeAudioBufferSize) && 
            _audioBufferPool.Count < 100)
        {
            Array.Clear(buffer, 0, buffer.Length); // Clear sensitive audio data
            _audioBufferPool.Enqueue(buffer);
            return;
        }
        
        // Return to array pool
        _bytePool.Return(buffer, clearArray: true);
        Interlocked.Add(ref _totalAllocatedBytes, -buffer.Length);
    }
    
    /// <summary>
    /// Rent a float buffer for audio processing
    /// </summary>
    public float[] RentProcessingBuffer(int minimumSize)
    {
        // Try custom pool first
        if (minimumSize <= ProcessingBufferSize && _processingBufferPool.TryDequeue(out var pooledBuffer))
        {
            return pooledBuffer;
        }
        
        // Fall back to array pool
        var buffer = _floatPool.Rent(minimumSize);
        Interlocked.Add(ref _totalAllocatedBytes, buffer.Length * sizeof(float));
        UpdatePeakMemoryUsage();
        
        return buffer;
    }
    
    /// <summary>
    /// Return a float buffer to the pool
    /// </summary>
    public void ReturnProcessingBuffer(float[] buffer)
    {
        if (buffer == null) return;
        
        // Return to custom pool if appropriate
        if ((buffer.Length == ProcessingBufferSize || buffer.Length == StandardAudioBufferSize / 2) && 
            _processingBufferPool.Count < 100)
        {
            Array.Clear(buffer, 0, buffer.Length);
            _processingBufferPool.Enqueue(buffer);
            return;
        }
        
        // Return to array pool
        _floatPool.Return(buffer, clearArray: true);
        Interlocked.Add(ref _totalAllocatedBytes, -buffer.Length * sizeof(float));
    }
    
    /// <summary>
    /// Rent a short buffer for 16-bit audio data
    /// </summary>
    public short[] RentShortBuffer(int minimumSize)
    {
        var buffer = _shortPool.Rent(minimumSize);
        Interlocked.Add(ref _totalAllocatedBytes, buffer.Length * sizeof(short));
        UpdatePeakMemoryUsage();
        return buffer;
    }
    
    /// <summary>
    /// Return a short buffer to the pool
    /// </summary>
    public void ReturnShortBuffer(short[] buffer)
    {
        if (buffer == null) return;
        
        _shortPool.Return(buffer, clearArray: true);
        Interlocked.Add(ref _totalAllocatedBytes, -buffer.Length * sizeof(short));
    }
    
    /// <summary>
    /// Get a pooled memory segment for high-performance scenarios
    /// </summary>
    public IMemoryOwner<T> RentMemory<T>(int minimumSize) where T : struct
    {
        return MemoryPool<T>.Shared.Rent(minimumSize);
    }
    
    /// <summary>
    /// Force garbage collection and compaction if memory usage is high
    /// </summary>
    public void ForceCleanup()
    {
        lock (_gcLock)
        {
            var currentMemory = GetCurrentMemoryUsage();
            var maxMemory = _config.MaxMemoryPoolSizeMB * 1024 * 1024;
            
            if (currentMemory > maxMemory * 0.8) // 80% threshold
            {
                _logger.LogWarning("Memory usage high ({currentMB}MB), forcing cleanup", currentMemory / (1024 * 1024));
                
                // Trim custom pools
                TrimCustomPools();
                
                // Force garbage collection
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                
                var newMemory = GetCurrentMemoryUsage();
                _logger.LogInformation("Cleanup completed. Memory usage: {oldMB}MB -> {newMB}MB", 
                    currentMemory / (1024 * 1024), newMemory / (1024 * 1024));
            }
        }
    }
    
    /// <summary>
    /// Trim custom pools to reduce memory usage
    /// </summary>
    private void TrimCustomPools()
    {
        // Trim audio buffer pool to 25% of current size
        int audioBuffersToRemove = _audioBufferPool.Count * 3 / 4;
        for (int i = 0; i < audioBuffersToRemove && _audioBufferPool.TryDequeue(out _); i++) { }
        
        // Trim processing buffer pool to 25% of current size
        int processingBuffersToRemove = _processingBufferPool.Count * 3 / 4;
        for (int i = 0; i < processingBuffersToRemove && _processingBufferPool.TryDequeue(out _); i++) { }
        
        _logger.LogDebug("Trimmed custom pools: removed {audioBuffers} audio buffers, {processingBuffers} processing buffers",
            audioBuffersToRemove, processingBuffersToRemove);
    }
    
    /// <summary>
    /// Optimize garbage collection based on current memory usage patterns
    /// </summary>
    private void OptimizeGarbageCollection(object? state)
    {
        if (!_config.EnableGCOptimizations) return;
        
        lock (_gcLock)
        {
            var now = DateTime.UtcNow;
            var timeSinceLastGc = now - _lastGcTime;
            
            // Check if we should optimize GC settings
            var currentMemory = GetCurrentMemoryUsage();
            var maxMemory = _config.MaxMemoryPoolSizeMB * 1024 * 1024;
            
            if (currentMemory > maxMemory * 0.6) // 60% threshold
            {
                // Switch to server GC mode for better throughput during heavy load
                if (GCSettings.IsServerGC == false)
                {
                    _logger.LogDebug("High memory usage detected, optimizing GC for throughput");
                }
                
                // Force a Gen 1 collection if it's been a while
                if (timeSinceLastGc > TimeSpan.FromMinutes(2))
                {
                    GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                    _lastGcTime = now;
                }
            }
            else if (currentMemory < maxMemory * 0.3) // 30% threshold
            {
                // Low memory usage - can be more aggressive with cleanup
                if (timeSinceLastGc > TimeSpan.FromMinutes(5))
                {
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
                    _lastGcTime = now;
                }
            }
        }
    }
    
    /// <summary>
    /// Update peak memory usage tracking
    /// </summary>
    private void UpdatePeakMemoryUsage()
    {
        var current = _totalAllocatedBytes + _poolAllocatedBytes;
        if (current > _peakMemoryUsage)
        {
            Interlocked.Exchange(ref _peakMemoryUsage, current);
        }
    }
    
    /// <summary>
    /// Get current memory usage in bytes
    /// </summary>
    public long GetCurrentMemoryUsage()
    {
        return _totalAllocatedBytes + _poolAllocatedBytes + GC.GetTotalMemory(forceFullCollection: false);
    }
    
    /// <summary>
    /// Get memory usage statistics
    /// </summary>
    public MemoryUsageStats GetMemoryStats()
    {
        return new MemoryUsageStats
        {
            TotalAllocatedBytes = _totalAllocatedBytes,
            PoolAllocatedBytes = _poolAllocatedBytes,
            PeakMemoryUsage = _peakMemoryUsage,
            AudioBuffersPooled = _audioBufferPool.Count,
            ProcessingBuffersPooled = _processingBufferPool.Count,
            GcTotalMemory = GC.GetTotalMemory(forceFullCollection: false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }
    
    /// <summary>
    /// Log memory usage statistics
    /// </summary>
    public void LogMemoryStats()
    {
        var stats = GetMemoryStats();
        _logger.LogInformation(
            "Memory Stats - Total: {totalMB}MB, Pool: {poolMB}MB, Peak: {peakMB}MB, " +
            "Audio Buffers: {audioBuffers}, Processing Buffers: {processingBuffers}, " +
            "GC Collections (0/1/2): {gen0}/{gen1}/{gen2}",
            stats.TotalAllocatedBytes / (1024 * 1024),
            stats.PoolAllocatedBytes / (1024 * 1024),
            stats.PeakMemoryUsage / (1024 * 1024),
            stats.AudioBuffersPooled,
            stats.ProcessingBuffersPooled,
            stats.Gen0Collections,
            stats.Gen1Collections,
            stats.Gen2Collections);
    }
    
    public void Dispose()
    {
        _gcOptimizationTimer?.Dispose();
        
        // Clear custom pools
        while (_audioBufferPool.TryDequeue(out _)) { }
        while (_processingBufferPool.TryDequeue(out _)) { }
        
        _logger.LogInformation("Memory manager disposed. Peak memory usage: {peakMB}MB", _peakMemoryUsage / (1024 * 1024));
    }
}

/// <summary>
/// Memory usage statistics
/// </summary>
public struct MemoryUsageStats
{
    public long TotalAllocatedBytes { get; set; }
    public long PoolAllocatedBytes { get; set; }
    public long PeakMemoryUsage { get; set; }
    public int AudioBuffersPooled { get; set; }
    public int ProcessingBuffersPooled { get; set; }
    public long GcTotalMemory { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

/// <summary>
/// Disposable wrapper for managed memory segments
/// </summary>
public sealed class ManagedMemorySegment<T> : IDisposable where T : struct
{
    private readonly OptimizedMemoryManager _manager;
    private T[]? _array;
    private bool _disposed;
    
    public ManagedMemorySegment(OptimizedMemoryManager manager, T[] array)
    {
        _manager = manager;
        _array = array;
    }
    
    public Memory<T> Memory => _disposed ? Memory<T>.Empty : _array.AsMemory();
    public Span<T> Span => _disposed ? Span<T>.Empty : _array.AsSpan();
    
    public void Dispose()
    {
        if (!_disposed && _array != null)
        {
            if (typeof(T) == typeof(byte))
            {
                _manager.ReturnAudioBuffer((byte[])(object)_array);
            }
            else if (typeof(T) == typeof(float))
            {
                _manager.ReturnProcessingBuffer((float[])(object)_array);
            }
            else if (typeof(T) == typeof(short))
            {
                _manager.ReturnShortBuffer((short[])(object)_array);
            }
            
            _array = null;
            _disposed = true;
        }
    }
}

/// <summary>
/// Extension methods for optimized memory operations
/// </summary>
public static class OptimizedMemoryExtensions
{
    /// <summary>
    /// Rent a managed memory segment that automatically returns to pool when disposed
    /// </summary>
    public static ManagedMemorySegment<T> RentManagedSegment<T>(this OptimizedMemoryManager manager, int minimumSize) where T : struct
    {
        T[] array;
        
        if (typeof(T) == typeof(byte))
        {
            array = (T[])(object)manager.RentAudioBuffer(minimumSize);
        }
        else if (typeof(T) == typeof(float))
        {
            array = (T[])(object)manager.RentProcessingBuffer(minimumSize);
        }
        else if (typeof(T) == typeof(short))
        {
            array = (T[])(object)manager.RentShortBuffer(minimumSize);
        }
        else
        {
            throw new NotSupportedException($"Type {typeof(T)} is not supported");
        }
        
        return new ManagedMemorySegment<T>(manager, array);
    }
}
