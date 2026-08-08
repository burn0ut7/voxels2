internal sealed class VoxelGpuResidentTable
{
	private readonly ResidentEntry[] _entries;
	private readonly Stack<int> _freeSlots;
	private readonly Dictionary<VoxelVisualBlockKey, int> _slotsByKey = new();

	public int Capacity => _entries.Length;
	public int Count => _slotsByKey.Count;
	public int PublishedCount => _entries.Count( entry => entry.Published );

	public VoxelGpuResidentTable( int capacity )
	{
		if ( capacity < 1 ) throw new System.ArgumentOutOfRangeException( nameof( capacity ) );
		_entries = new ResidentEntry[capacity];
		_freeSlots = new Stack<int>( capacity );
		for ( var slot = capacity - 1; slot >= 0; slot-- ) _freeSlots.Push( slot );
	}

	public bool TryReserve( VoxelVisualBlockKey key, uint generation, out int slot )
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

	public bool TryPublish( int slot, VoxelVisualBlockKey key, uint generation, VoxelGpuAllocationHandle allocation, VoxelGpuResidentDescriptor descriptor, out VoxelGpuAllocationHandle replaced )
	{
		replaced = default;
		if ( (uint)slot >= (uint)_entries.Length ) return false;
		var entry = _entries[slot];
		if ( entry.Key != key || generation < entry.Generation ) return false;
		if ( entry.Published ) replaced = entry.Allocation;
		_entries[slot] = new ResidentEntry( key, generation, allocation, descriptor, true );
		return true;
	}

	public bool TryRemove( VoxelVisualBlockKey key, out VoxelGpuAllocationHandle allocation )
	{
		allocation = default;
		if ( !_slotsByKey.Remove( key, out var slot ) ) return false;
		var entry = _entries[slot];
		if ( entry.Published ) allocation = entry.Allocation;
		_entries[slot] = default;
		_freeSlots.Push( slot );
		return true;
	}

	public void CancelUnpublishedReservation( VoxelVisualBlockKey key )
	{
		if ( !_slotsByKey.TryGetValue( key, out var slot ) || _entries[slot].Published ) return;
		_slotsByKey.Remove( key );
		_entries[slot] = default;
		_freeSlots.Push( slot );
	}

	public IEnumerable<(int Slot, ResidentEntry Entry)> PublishedEntries()
	{
		for ( var slot = 0; slot < _entries.Length; slot++ )
		{
			if ( _entries[slot].Published ) yield return (slot, _entries[slot]);
		}
	}

	internal readonly record struct ResidentEntry(
		VoxelVisualBlockKey Key,
		uint Generation,
		VoxelGpuAllocationHandle Allocation,
		VoxelGpuResidentDescriptor Descriptor,
		bool Published );
}
