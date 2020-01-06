﻿#region BSD License
/* Copyright (c) 2013-2018, Doxense SAS
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of Doxense nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

namespace FoundationDB.Client
{
	using JetBrains.Annotations;
	using System;
	using System.Collections.Concurrent;
	using System.Diagnostics;
	using System.Threading;
	using System.Threading.Tasks;
	using Doxense.Diagnostics.Contracts;
	using Doxense.Threading.Tasks;
	using FoundationDB.Client.Core;
	using FoundationDB.Client.Native;
	using FoundationDB.DependencyInjection;

	/// <summary>FoundationDB database session handle</summary>
	/// <remarks>An instance of this class can be used to create any number of concurrent transactions that will read and/or write to this particular database.</remarks>
	[DebuggerDisplay("Root={Root}")]
	public class FdbDatabase : IFdbDatabase, IFdbDatabaseProvider
	{
		#region Private Fields...

		/// <summary>Underlying handler for this database (native, dummy, memory, ...)</summary>
		private readonly IFdbDatabaseHandler m_handler;

		/// <summary>If true, the database will only allow read-only transactions.</summary>
		private bool m_readOnly;

		/// <summary>Global cancellation source that is cancelled when the current db instance gets disposed.</summary>
		private readonly CancellationTokenSource m_cts = new CancellationTokenSource();

		/// <summary>Set to true when the current db instance gets disposed.</summary>
		private volatile bool m_disposed;

		/// <summary>Global counters used to generate the transaction's local id (for debugging purpose)</summary>
		private static int s_transactionCounter;

		/// <summary>List of all "pending" transactions created from this database instance (and that have not yet been disposed)</summary>
		private readonly ConcurrentDictionary<int, FdbTransaction> m_transactions = new ConcurrentDictionary<int, FdbTransaction>();

		/// <summary>DirectoryLayer instance used by this database</summary>
		private FdbDirectoryLayer m_directory;

		/// <summary>The root location of this database</summary>
		private DynamicKeySubspaceLocation m_root;

		/// <summary>Default Timeout value for all transactions</summary>
		private int m_defaultTimeout;

		/// <summary>Default Retry Limit value for all transactions</summary>
		private int m_defaultRetryLimit;

		/// <summary>Default Max Retry Delay value for all transactions</summary>
		private int m_defaultMaxRetryDelay;

		#endregion

		#region Constructors...

		/// <summary>Create a new database instance</summary>
		/// <param name="handler">Handle to the native FDB_DATABASE*</param>
		/// <param name="root">Root location of this database</param>
		/// <param name="directory">DirectoryLayer instance used by this database</param>
		/// <param name="readOnly">If true, the database instance will only allow read-only transactions</param>
		protected FdbDatabase(IFdbDatabaseHandler handler, [CanBeNull] DynamicKeySubspaceLocation root, FdbDirectoryLayer directory, bool readOnly)
		{
			Contract.Requires(handler != null);

			m_handler = handler;
			m_readOnly = readOnly;
			ChangeRoot(root ?? SubspaceLocation.Empty, directory, readOnly);
		}

		/// <summary>Create a new Database instance from a database handler</summary>
		/// <param name="handler">Handle to the native FDB_DATABASE*</param>
		/// <param name="root">Root location of the database</param>
		/// <param name="directory">DirectoryLayer instance used by the database</param>
		/// <param name="readOnly">If true, the database instance will only allow read-only transactions</param>
		public static FdbDatabase Create([NotNull] IFdbDatabaseHandler handler, [NotNull] ISubspaceLocation root, [NotNull] FdbDirectoryLayer directory, bool readOnly)
		{
			Contract.NotNull(handler, nameof(handler));
			Contract.NotNull(root, nameof(root));
			Contract.NotNull(directory, nameof(directory));

			return new FdbDatabase(handler, root.AsDynamic(), directory, readOnly);
		}

		#endregion

		#region Public Properties...

		string IFdbDatabase.Name => "DB";

		public string ClusterFile => m_handler.ClusterFile;

		/// <summary>Returns a cancellation token that is linked with the lifetime of this database instance</summary>
		/// <remarks>The token will be cancelled if the database instance is disposed</remarks>
		//REVIEW: rename this to 'Cancellation'? ('Token' is a keyword that may have different meaning in some apps)
		public CancellationToken Cancellation => m_cts.Token;

		/// <summary>If true, this database instance will only allow starting read-only transactions.</summary>
		public bool IsReadOnly => m_readOnly;

		/// <summary>Root directory of this database instance</summary>
		public IFdbDirectory Directory => m_directory;

		/// <summary>Return the DirectoryLayer instance used by this database</summary>
		internal FdbDirectoryLayer GetDirectoryLayer() => m_directory;

		#endregion

		#region Transaction Management...

		/// <summary>Start a new transaction on this database</summary>
		/// <param name="mode">Mode of the new transaction (read-only, read-write, ...)</param>
		/// <param name="ct">Optional cancellation token that can abort all pending async operations started by this transaction.</param>
		/// <param name="context">If not null, attach the new transaction to an existing context.</param>
		/// <returns>New transaction instance that can read from or write to the database.</returns>
		/// <remarks>You MUST call Dispose() on the transaction when you are done with it. You SHOULD wrap it in a 'using' statement to ensure that it is disposed in all cases.</remarks>
		/// <example>
		/// using(var tr = db.BeginTransaction(CancellationToken.None))
		/// {
		///		tr.Set(Slice.FromString("Hello"), Slice.FromString("World"));
		///		tr.Clear(Slice.FromString("OldValue"));
		///		await tr.CommitAsync();
		/// }</example>
		public async ValueTask<IFdbTransaction> BeginTransactionAsync(FdbTransactionMode mode, CancellationToken ct, FdbOperationContext context = null)
		{
			ct.ThrowIfCancellationRequested();
			if (context == null) context = new FdbOperationContext(this, mode, ct);
			var tr = CreateNewTransaction(context);
			await context.ComputeRoot(tr);
			return tr;
		}

		/// <summary>Start a new transaction on this database, with an optional context</summary>
		/// <param name="context">Optional context in which the transaction will run</param>
		internal FdbTransaction CreateNewTransaction(FdbOperationContext context)
		{
			Contract.Requires(context?.Database != null);
			ThrowIfDisposed();

			// force the transaction to be read-only, if the database itself is read-only
			var mode = context.Mode;
			if (m_readOnly) mode |= FdbTransactionMode.ReadOnly;

			int id = Interlocked.Increment(ref s_transactionCounter);

			// ensure that if anything happens, either we return a valid Transaction, or we dispose it immediately
			FdbTransaction trans = null;
			try
			{
				var transactionHandler = m_handler.CreateTransaction(context);

				trans = new FdbTransaction(this, context, id, transactionHandler, mode);
				RegisterTransaction(trans);
				context.AttachTransaction(trans);
				// set default options..
				if (m_defaultTimeout != 0) trans.Timeout = m_defaultTimeout;
				if (m_defaultRetryLimit != 0) trans.RetryLimit = m_defaultRetryLimit;
				if (m_defaultMaxRetryDelay != 0) trans.MaxRetryDelay = m_defaultMaxRetryDelay;
				// flag as ready
				trans.State = FdbTransaction.STATE_READY;
				return trans;
			}
			catch (Exception)
			{
				if (trans != null)
				{
					context.ReleaseTransaction(trans);
					trans.Dispose();
				}
				throw;
			}
		}

		internal void EnsureTransactionIsValid(FdbTransaction transaction)
		{
			Contract.Requires(transaction != null);
			if (m_disposed) ThrowIfDisposed();
			//TODO?
		}

		/// <summary>Add a new transaction to the list of tracked transactions</summary>
		internal void RegisterTransaction(FdbTransaction transaction)
		{
			Contract.Requires(transaction != null);

			if (!m_transactions.TryAdd(transaction.Id, transaction))
			{
				throw Fdb.Errors.FailedToRegisterTransactionOnDatabase(transaction, this);
			}
		}

		/// <summary>Remove a transaction from the list of tracked transactions</summary>
		/// <param name="transaction"></param>
		internal void UnregisterTransaction(FdbTransaction transaction)
		{
			Contract.Requires(transaction != null);

			//do nothing is already disposed
			if (m_disposed) return;

			// Unregister the transaction. We do not care if it has already been done
			FdbTransaction _;
			m_transactions.TryRemove(transaction.Id, out _);
			//TODO: compare removed value with the specified transaction to ensure it was the correct one?
		}

		#endregion

		#region Transactionals...

		//NOTE: other bindings use different names or concept for transactionals, and some also support ReadOnly vs ReadWrite transaction
		// - Python uses the @transactional decorator with first arg called db_or_trans
		// - JAVA uses db.run() and db.runAsync(), but does not have a method for read-only transactions
		// - Ruby uses db.transact do |tr|
		// - Go uses db.Transact(...) and db.ReadTransact(...)
		// - NodeJS uses fdb.doTransaction(function(...) { ... })

		// Conventions:
		// - ReadAsync() => read-only
		// - WriteAsync() => write-only
		// - ReadWriteAsync() => read/write

		#region IFdbReadOnlyTransactional methods...

		/// <summary>Runs a transactional lambda function against this database, inside a read-only transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-only transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadAsync<TResult>(Func<IFdbReadOnlyTransaction, Task<TResult>> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TResult>(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-only transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-only transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task ReadAsync(Func<IFdbReadOnlyTransaction, Task> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-only transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-only transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadAsync<TResult>(Func<IFdbReadOnlyTransaction, ValueTask<TResult>> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TResult>(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-only transaction context, with retry logic.</summary>
		/// <param name="state">State that will be passed back to the <paramref name="handler"/></param>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-only transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadAsync<TState, TResult>(TState state, Func<IFdbReadOnlyTransaction, TState, Task<TResult>> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TResult>(this, (tr) => handler(tr, state), ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-only transaction context, with retry logic.</summary>
		/// <param name="state">State that will be passed back to the <paramref name="handler"/></param>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-only transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadAsync<TState, TResult>(TState state, Func<IFdbReadOnlyTransaction, TState, ValueTask<TResult>> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TResult>(this, (tr) => handler(tr, state), ct);
		}

		public Task<TResult> ReadAsync<TResult>([InstantHandle] Func<IFdbReadOnlyTransaction, Task<TResult>> handler, [InstantHandle] Action<IFdbReadOnlyTransaction, TResult> success, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TResult>(this, handler, success, ct);
		}

		public Task<TResult> ReadAsync<TIntermediate, TResult>([InstantHandle] Func<IFdbReadOnlyTransaction, Task<TIntermediate>> handler, [InstantHandle] Func<IFdbReadOnlyTransaction, TIntermediate, TResult> success, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TIntermediate, TResult>(this, handler, success, ct);
		}

		public Task<TResult> ReadAsync<TIntermediate, TResult>([InstantHandle] Func<IFdbReadOnlyTransaction, Task<TIntermediate>> handler, [InstantHandle] Func<IFdbReadOnlyTransaction, TIntermediate, Task<TResult>> success, CancellationToken ct)
		{
			return FdbOperationContext.RunReadWithResultAsync<TIntermediate, TResult>(this, handler, success, ct);
		}

		#endregion

		#region IFdbTransactional methods...

		#region Write Only...

		/// <summary>Runs a transactional lambda function against this database, inside a write-only transaction context, with retry logic.</summary>
		/// <param name="handler">Lambda function that is passed a new read-write transaction on each retry. It should only call non-async methods, such as Set, Clear or any atomic operation.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task WriteAsync([InstantHandle] Action<IFdbTransaction> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a write-only transaction context, with retry logic.</summary>
		/// <param name="state">State that will be passed back to the <paramref name="handler"/></param>
		/// <param name="handler">Lambda function that is passed a new read-write transaction on each retry. It should only call non-async methods, such as Set, Clear or any atomic operation.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task WriteAsync<TState>(TState state, [InstantHandle] Action<IFdbTransaction, TState> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync(this, (tr) => handler(tr, state), ct);
		}

		/// <summary>EXPERIMENTAL</summary>
		public Task WriteAsync([InstantHandle] Action<IFdbTransaction> handler, [InstantHandle] Action<IFdbTransaction> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync(this, handler, success, ct);
		}

		/// <summary>EXPERIMENTAL</summary>
		public Task WriteAsync([InstantHandle] Action<IFdbTransaction> handler, [InstantHandle] Func<IFdbTransaction, Task> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync(this, handler, success, ct);
		}

		/// <summary>EXPERIMENTAL</summary>
		public Task<TResult> WriteAsync<TResult>([InstantHandle] Action<IFdbTransaction> handler, [InstantHandle] Func<IFdbTransaction, TResult> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync<TResult>(this, handler, success, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a write-only transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task WriteAsync([InstantHandle] Func<IFdbTransaction, Task> handler, CancellationToken ct)
		{
			//REVIEW: right now, nothing prevents the lambda from calling read methods on the transaction, making this equivalent to calling ReadWriteAsync()
			// => this version of WriteAsync is only there to catch mistakes when someones passes in an async lambda, instead of an Action<IFdbTransaction>
			//TODO: have a "WriteOnly" mode on transaction to forbid doing any reads ?
			return FdbOperationContext.RunWriteAsync(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a write-only transaction context, with retry logic.</summary>
		/// <param name="state">State that will be passed back to the <paramref name="handler"/></param>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task WriteAsync<TState>(TState state, [InstantHandle] Func<IFdbTransaction, TState, Task> handler, CancellationToken ct)
		{
			//REVIEW: right now, nothing prevents the lambda from calling read methods on the transaction, making this equivalent to calling ReadWriteAsync()
			// => this version of WriteAsync is only there to catch mistakes when someones passes in an async lambda, instead of an Action<IFdbTransaction>
			//TODO: have a "WriteOnly" mode on transaction to forbid doing any reads ?
			return FdbOperationContext.RunWriteAsync(this, (tr) => handler(tr, state), ct);
		}

		/// <summary>EXPERIMENTAL</summary>
		public Task WriteAsync([InstantHandle] Func<IFdbTransaction, Task> handler, [InstantHandle] Action<IFdbTransaction> success, CancellationToken ct)
		{
			//REVIEW: right now, nothing prevents the lambda from calling read methods on the transaction, making this equivalent to calling ReadWriteAsync()
			// => this version of WriteAsync is only there to catch mistakes when someones passes in an async lambda, instead of an Action<IFdbTransaction>
			//TODO: have a "WriteOnly" mode on transaction to forbid doing any reads ?
			return FdbOperationContext.RunWriteAsync(this, handler, success, ct);
		}

		/// <summary>EXPERIMENTAL</summary>
		public Task WriteAsync([InstantHandle] Func<IFdbTransaction, Task> handler, [InstantHandle] Func<IFdbTransaction, Task> success, CancellationToken ct)
		{
			//REVIEW: right now, nothing prevents the lambda from calling read methods on the transaction, making this equivalent to calling ReadWriteAsync()
			// => this version of WriteAsync is only there to catch mistakes when someones passes in an async lambda, instead of an Action<IFdbTransaction>
			//TODO: have a "WriteOnly" mode on transaction to forbid doing any reads ?
			return FdbOperationContext.RunWriteAsync(this, handler, success, ct);
		}

		#endregion

		#region Read+Write....

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task ReadWriteAsync([InstantHandle] Func<IFdbTransaction, Task> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="state">State that will be passed back to the <paramref name="handler"/></param>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task ReadWriteAsync<TState>(TState state, [InstantHandle] Func<IFdbTransaction, TState, Task> handler, CancellationToken ct)
		{
			Contract.NotNull(handler, nameof(handler));
			return FdbOperationContext.RunWriteAsync(this, (tr) => handler(tr, state), ct);
		}

		/// <summary>EXPERIMENTAL</summary>
		public Task ReadWriteAsync([InstantHandle] Func<IFdbTransaction, Task> handler, [InstantHandle] Action<IFdbTransaction> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteAsync(this, handler, success, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TResult>([InstantHandle] Func<IFdbTransaction, TResult> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TResult>(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TResult>([InstantHandle] Func<IFdbTransaction, Task<TResult>> handler, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TResult>(this, handler, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="state">State that will be passed back to the <paramref name="handler"/></param>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TState, TResult>(TState state, [InstantHandle] Func<IFdbTransaction, TState, Task<TResult>> handler, CancellationToken ct)
		{
			Contract.NotNull(handler, nameof(handler));
			return FdbOperationContext.RunWriteWithResultAsync<TResult>(this, (tr) => handler(tr, state), ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="success">Will be called at most once, and only if the transaction commits successfully. Any exception or crash that happens right after the commit may cause this callback not NOT be called, even if the transaction has committed!</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TResult>([InstantHandle] Func<IFdbTransaction, Task<TResult>> handler, [InstantHandle] Action<IFdbTransaction, TResult> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TResult>(this, handler, success, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="success">Will be called at most once, and only if the transaction commits successfully. Any exception or crash that happens right after the commit may cause this callback not NOT be called, even if the transaction has committed!</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TResult>([InstantHandle] Func<IFdbTransaction, Task> handler, [InstantHandle] Func<IFdbTransaction, TResult> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TResult>(this, handler, success, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="success">Will be called at most once, and only if the transaction commits successfully. Any exception or crash that happens right after the commit may cause this callback not NOT be called, even if the transaction has committed!</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TResult>([InstantHandle] Func<IFdbTransaction, Task> handler, [InstantHandle] Func<IFdbTransaction, Task<TResult>> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TResult>(this, handler, success, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="success">Will be called at most once, and only if the transaction commits successfully. Any exception or crash that happens right after the commit may cause this callback not NOT be called, even if the transaction has committed!</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TIntermediate, TResult>([InstantHandle] Func<IFdbTransaction, Task<TIntermediate>> handler, [InstantHandle] Func<IFdbTransaction, TIntermediate, TResult> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TIntermediate, TResult>(this, handler, success, ct);
		}

		/// <summary>Runs a transactional lambda function against this database, inside a read-write transaction context, with retry logic.</summary>
		/// <param name="handler">Asynchronous lambda function that is passed a new read-write transaction on each retry. The result of the task will also be the result of the transactional.</param>
		/// <param name="success">Will be called at most once, and only if the transaction commits successfully. Any exception or crash that happens right after the commit may cause this callback not NOT be called, even if the transaction has committed!</param>
		/// <param name="ct">Optional cancellation token that will be passed to the transaction context, and that can also be used to abort the retry loop.</param>
		public Task<TResult> ReadWriteAsync<TIntermediate, TResult>([InstantHandle] Func<IFdbTransaction, Task<TIntermediate>> handler, [InstantHandle] Func<IFdbTransaction, TIntermediate, Task<TResult>> success, CancellationToken ct)
		{
			return FdbOperationContext.RunWriteWithResultAsync<TIntermediate, TResult>(this, handler, success, ct);
		}

		#endregion

		#endregion

		#endregion

		#region Database Options...

		/// <summary>Set a parameter-less option on this database</summary>
		/// <param name="option">Option to set</param>
		public void SetOption(FdbDatabaseOption option)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option}");

			m_handler.SetOption(option, default);
		}

		/// <summary>Set an option on this database that takes a string value</summary>
		/// <param name="option">Option to set</param>
		/// <param name="value">Value of the parameter (can be null)</param>
		public void SetOption(FdbDatabaseOption option, string value)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option} to '{value ?? "<null>"}'");

			var data = FdbNative.ToNativeString(value.AsSpan(), nullTerminated: true);
			m_handler.SetOption(option, data);
		}

		/// <summary>Set an option on this database that takes an integer value</summary>
		/// <param name="option">Option to set</param>
		/// <param name="value">Value of the parameter</param>
		public void SetOption(FdbDatabaseOption option, long value)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option} to {value}");

			// Spec says: "If the option is documented as taking an Int parameter, value must point to a signed 64-bit integer (little-endian), and value_length must be 8."
			var data = Slice.FromFixed64(value);
			m_handler.SetOption(option, data);
		}

		#endregion

		#region Key Space Management...

		/// <summary>Change the current global namespace.</summary>
		/// <remarks>Do NOT call this, unless you know exactly what you are doing !</remarks>
		internal void ChangeRoot(DynamicKeySubspaceLocation root, FdbDirectoryLayer directory, bool readOnly)
		{
			Contract.NotNull(root, nameof(root));
			Contract.NotNull(directory, nameof(directory));

			lock (this)//TODO: don't use this for locking
			{
				m_readOnly = readOnly;
				m_root = root;
				m_directory = directory;
			}
		}

		public DynamicKeySubspaceLocation Root => m_root;

		/// <summary>Ensures that a serialized value is valid</summary>
		/// <remarks>Throws an exception if the value is null, or exceeds the maximum allowed size (Fdb.MaxValueSize)</remarks>
		internal void EnsureValueIsValid(ReadOnlySpan<byte> value)
		{
			var ex = ValidateValue(value);
			if (ex != null) throw ex;
		}

		internal Exception ValidateValue(ReadOnlySpan<byte> value)
		{
			if (value.Length > Fdb.MaxValueSize)
			{
				return Fdb.Errors.ValueIsTooBig(value);
			}

			return null;
		}

		#endregion

		#region Default Transaction Settings...

		/// <summary>Default Timeout value (in milliseconds) for all transactions created from this database instance.</summary>
		/// <remarks>Only effective for future transactions</remarks>
		public int DefaultTimeout
		{
			get => m_defaultTimeout;
			set
			{
				if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Timeout value cannot be negative");
				m_defaultTimeout = value;
			}
		}

		/// <summary>Default Retry Limit value for all transactions created from this database instance.</summary>
		/// <remarks>Only effective for future transactions</remarks>
		public int DefaultRetryLimit
		{
			get => m_defaultRetryLimit;
			set
			{
				if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "RetryLimit value cannot be negative");
				m_defaultRetryLimit = value;
			}
		}

		/// <summary>Default Max Retry Delay value for all transactions created from this database instance.</summary>
		/// <remarks>Only effective for future transactions</remarks>
		public int DefaultMaxRetryDelay
		{
			get => m_defaultMaxRetryDelay;
			set
			{
				if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetryDelay value cannot be negative");
				m_defaultMaxRetryDelay = value;
			}
		}

		#endregion

		#region IDisposable...

		private void ThrowIfDisposed()
		{
			if (m_disposed) throw new ObjectDisposedException(this.GetType().Name);
		}

		/// <summary>Close this database instance, aborting any pending transaction that was created by this instance.</summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>Close this database instance, aborting any pending transaction that was created by this instance.</summary>
		protected virtual void Dispose(bool disposing)
		{
			if (!m_disposed)
			{
				m_disposed = true;
				if (disposing)
				{
					try
					{
						// mark this db has dead, but keep the handle alive until after all the callbacks have fired

						//TODO: kill all pending transactions on this db?
						foreach (var trans in m_transactions.Values)
						{
							if (trans != null && trans.StillAlive)
							{
								trans.Cancel();
							}
						}
						m_transactions.Clear();

						//note: will block until all the registered callbacks have finished executing
						m_cts.SafeCancelAndDispose();
					}
					finally
					{
						if (m_handler != null)
						{
							if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "Dispose", $"Disposing database handler");
							try { m_handler.Dispose(); }
							catch (Exception e)
							{
								if (Logging.On) Logging.Exception(this, "Dispose", e);
							}
						}
					}
				}
			}
		}

		#endregion

		#region IFdbDatabaseProvider...

		IFdbDatabaseScopeProvider IFdbDatabaseScopeProvider.Parent { get; }

		ValueTask<IFdbDatabase> IFdbDatabaseScopeProvider.GetDatabase(CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			return new ValueTask<IFdbDatabase>(this);
		}

		public IFdbDatabaseScopeProvider<TState> CreateScope<TState>(Func<IFdbDatabase, CancellationToken, Task<(IFdbDatabase Db, TState State)>> start, CancellationToken lifetime = default)
		{
			Contract.NotNull(start, nameof(start));
			return new FdbDatabaseScopeProvider<TState>(this, start, lifetime);
		}

		bool IFdbDatabaseScopeProvider.IsAvailable => true;

		void IFdbDatabaseProvider.Start()
		{
			//NOP
		}

		void IFdbDatabaseProvider.Stop()
		{
			//NOP
		}

		#endregion

	}

}
