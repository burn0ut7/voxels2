internal sealed class VoxelGpuResidentTable
{
	private readonly ResidentEntry[] _entries;
	private readonly Stack<int> _freeSlots;
	private readonly Dictionary<VoxelVisualBlockKey, int> _slotsByKey = new();
	private readonly object _sync = new();
	private int _publishedCount;

	public int Capacity => _entries.Length;
	public int Count { get { lock ( _sync ) return _slotsByKey.Count; } }
	public int PublishedCount { get { lock ( _sync ) return _publishedCount; } }
	public bool ContainsKey( VoxelVisualBlockKey key ) { lock ( _sync ) return _slotsByKey.ContainsKey( key ); }
	public bool TryGetPublished( VoxelVisualBlockKey key, out ResidentEntry entry )
	{
		lock ( _sync )
		{
			if ( !_slotsByKey.TryGetValue( key, out var slot ) || !_entries[slot].Published )
			{
				entry = default;
				return false;
			}
			entry = _entries[slot];
			return true;
		}
	}

	public VoxelGpuResidentTable( int capacity )
	{
		if ( capacity < 1 ) throw new System.ArgumentOutOfRangeException( nameof( capacity ) );
		_entries = new ResidentEntry[capacity];
		_freeSlots = new Stack<int>( capacity );
		for ( var slot = capacity - 1; slot >= 0; slot-- ) _freeSlots.Push( slot );
	}

	public bool TryReserve( VoxelVisualBlockKey key, uint generation, out int slot )
	{
		lock ( _sync )
		{
			if ( _slotsByKey.TryGetValue( key, out slot ) )
			{
				return generation > _entries[slot].Generation;
			}
			if ( _freeSlots.Count == 0 ) return false;
			slot = _freeSlots.Pop();
			_slotsByKey.Add( key, slot );
			_entries[slot] = new ResidentEntry( key, generation, default, default, false );
			return true;
		}
	}

	public bool TryPublish( int slot, VoxelVisualBlockKey key, uint generation, VoxelGpuAllocationHandle allocation, VoxelGpuResidentDescriptor descriptor, out VoxelGpuAllocationHandle replaced )
	{
		lock ( _sync )
		{
			replaced = default;
			if ( (uint)slot >= (uint)_entries.Length ) return false;
			var entry = _entries[slot];
			if ( entry.Key != key || generation < entry.Generation ) return false;
			if ( entry.Published ) replaced = entry.Allocation;
			else _publishedCount++;
			_entries[slot] = new ResidentEntry( key, generation, allocation, descriptor, true );
			return true;
		}
	}

	public bool TryRemove( VoxelVisualBlockKey key, out VoxelGpuAllocationHandle allocation )
	{
		lock ( _sync )
		{
			allocation = default;
			if ( !_slotsByKey.Remove( key, out var slot ) ) return false;
			var entry = _entries[slot];
			if ( entry.Published )
			{
				allocation = entry.Allocation;
				_publishedCount--;
			}
			_entries[slot] = default;
			_freeSlots.Push( slot );
			return true;
		}
	}

	public void CancelUnpublishedReservation( VoxelVisualBlockKey key )
	{
		lock ( _sync )
		{
			if ( !_slotsByKey.TryGetValue( key, out var slot ) || _entries[slot].Published ) return;
			_slotsByKey.Remove( key );
			_entries[slot] = default;
			_freeSlots.Push( slot );
		}
	}

	public int CopyPublishedEntries( ResidentEntry[] destination )
	{
		if ( destination is null || destination.Length < _entries.Length ) throw new System.ArgumentException( "Destination is smaller than the resident table.", nameof( destination ) );
		lock ( _sync )
		{
			var count = 0;
			for ( var slot = 0; slot < _entries.Length; slot++ )
			{
				var entry = _entries[slot];
				if ( !entry.Published ) continue;
				destination[count++] = entry;
			}
			return count;
		}
	}

	internal readonly record struct ResidentEntry(
		VoxelVisualBlockKey Key,
		uint Generation,
		VoxelGpuAllocationHandle Allocation,
		VoxelGpuResidentDescriptor Descriptor,
		bool Published );
}
