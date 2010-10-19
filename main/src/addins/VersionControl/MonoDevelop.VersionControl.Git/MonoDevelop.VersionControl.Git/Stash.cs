// 
// Stash.cs
//  
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
// 
// Copyright (c) 2010 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using NGit;
using NGit.Merge;
using NGit.Treewalk;
using NGit.Diff;
using NGit.Dircache;
using NGit.Revwalk;

namespace MonoDevelop.VersionControl.Git
{
	public class Stash
	{
		internal string PrevStashCommitId { get; private set; }
		internal string CommitId { get; private set; }
		internal string FullLine { get; private set; }
		internal StashCollection StashCollection { get; set; }
		
		/// <summary>
		/// Who created the stash
		/// </summary>
		public PersonIdent Author { get; private set; }
		
		/// <summary>
		/// Timestamp of the stash creation
		/// </summary>
		public DateTimeOffset DateTime { get; private set; }
		
		/// <summary>
		/// Stash comment
		/// </summary>
		public string Comment { get; private set; }
		
		private Stash ()
		{
		}
		
		internal Stash (string prevStashCommitId, string commitId, PersonIdent author, string comment)
		{
			this.PrevStashCommitId = prevStashCommitId;
			this.CommitId = commitId;
			this.Author = author;
			this.DateTime = DateTimeOffset.Now;
			
			// Skip "WIP on master: "
			int i = comment.IndexOf (':');
			this.Comment = comment.Substring (i + 2);			
			
			// Create the text line to be written in the stash log
			
			int secs = (int) (this.DateTime - new DateTimeOffset (1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
			Console.WriteLine ();
			
			TimeSpan ofs = this.DateTime.Offset;
			string tz = string.Format ("{0}{1:00}{2:00}", (ofs.Hours >= 0 ? '+':'-'), Math.Abs (ofs.Hours), Math.Abs (ofs.Minutes));
			
			StringBuilder sb = new StringBuilder ();
			sb.Append (prevStashCommitId ?? new string ('0', 40)).Append (' ');
			sb.Append (commitId).Append (' ');
			sb.Append (author.GetName ()).Append (" <").Append (author.GetEmailAddress ()).Append ("> ");
			sb.Append (secs).Append (' ').Append (tz).Append ('\t');
			sb.Append (comment);
			FullLine = sb.ToString ();
		}

		
		internal static Stash Parse (string line)
		{
			// Parses a stash log line and creates a Stash object with the information
			
			Stash s = new Stash ();
			s.PrevStashCommitId = line.Substring (0, 40);
			if (s.PrevStashCommitId.All (c => c == '0')) // And id will all 0 means no parent (first stash of the stack)
				s.PrevStashCommitId = null;
			s.CommitId = line.Substring (41, 40);
			
			string aname = string.Empty;
			string amail = string.Empty;
			
			int i = line.IndexOf ('<');
			if (i != -1) {
				aname = line.Substring (82, i - 82 - 1);
				i++;
				int i2 = line.IndexOf ('>', i);
				if (i2 != -1)
					amail = line.Substring (i, i2 - i);
				
				i2 += 2;
				i = line.IndexOf (' ', i2);
				int secs = int.Parse (line.Substring (i2, i - i2));
				DateTime t = new DateTime (1970, 1, 1) + TimeSpan.FromSeconds (secs);
				string st = t.ToString ("yyyy-MM-ddTHH:mm:ss") + line.Substring (i + 1, 3) + ":" + line.Substring (i + 4, 2);
				s.DateTime = DateTimeOffset.Parse (st);
				s.Comment = line.Substring (i + 7);
				i = s.Comment.IndexOf (':');
				if (i != -1)
					s.Comment = s.Comment.Substring (i + 2);			
			}
			s.Author = new PersonIdent (aname, amail);
			s.FullLine = line;
			return s;
		}
		
		public void Apply ()
		{
			StashCollection.Apply (this);
		}
	}
	
	public class StashCollection: IEnumerable<Stash>
	{
		NGit.Repository _repo;
		
		internal StashCollection (NGit.Repository repo)
		{
			this._repo = repo;
		}
		
		FileInfo StashLogFile {
			get {
				string stashLog = Path.Combine (_repo.Directory, "logs");
				stashLog = Path.Combine (stashLog, "refs");
				return new FileInfo (Path.Combine (stashLog, "stash"));
			}
		}
		
		FileInfo StashRefFile {
			get {
				string file = Path.Combine (_repo.Directory, "refs");
				return new FileInfo (Path.Combine (file, "stash"));
			}
		}
		
		public Stash Create ()
		{
			return Create (null);
		}
		
		public Stash Create (string message)
		{
			UserConfig config = _repo.GetConfig ().Get (UserConfig.KEY);
			RevWalk rw = new RevWalk (_repo);
			ObjectId headId = _repo.Resolve (Constants.HEAD);
			var parent = rw.ParseCommit (headId);
			
			PersonIdent author = new PersonIdent(config.GetAuthorName () ?? "unknown", config.GetAuthorEmail () ?? "unknown@(none).");
			
			if (string.IsNullOrEmpty (message)) {
				// Use the commit summary as message
				message = parent.Abbreviate (7).ToString () + " " + parent.GetShortMessage ();
				int i = message.IndexOfAny (new char[] { '\r', '\n' });
				if (i != -1)
					message = message.Substring (0, i);
			}
			
			// Create the index tree commit
			GitIndex index = _repo.GetIndex ();
			index.RereadIfNecessary();
			var tree_id = index.WriteTree ();
			string commitMsg = "index on " + _repo.GetBranch () + ": " + message;
			ObjectId indexCommit = GitUtil.CreateCommit (_repo, commitMsg + "\n", new ObjectId[] {headId}, tree_id, author, author);

			// Create the working dir commit
			tree_id = WriteWorkingDirectoryTree (parent.Tree, index);
			commitMsg = "WIP on " + _repo.GetBranch () + ": " + message;
			var wipCommit = GitUtil.CreateCommit(_repo, commitMsg + "\n", new ObjectId[] { headId, indexCommit }, tree_id, author, author);
			
			string prevCommit = null;
			FileInfo sf = StashRefFile;
			if (sf.Exists)
				prevCommit = File.ReadAllText (sf.FullName);
			
			Stash s = new Stash (prevCommit, wipCommit.Name, author, commitMsg);
			
			FileInfo stashLog = StashLogFile;
			File.AppendAllText (stashLog.FullName, s.FullLine + "\n");
			File.WriteAllText (sf.FullName, s.CommitId + "\n");
			
			// Wipe all local changes
			GitUtil.HardReset (_repo, Constants.HEAD);
			
			s.StashCollection = this;
			return s;
		}
		
		ObjectId WriteWorkingDirectoryTree (RevTree headTree, GitIndex index)
		{
			DirCache dc = DirCache.NewInCore ();
			DirCacheBuilder cb = dc.Builder ();
			
			ObjectInserter oi = _repo.NewObjectInserter ();
			try {
				TreeWalk tw = new TreeWalk (_repo);
				tw.Reset ();
				tw.AddTree (new FileTreeIterator (_repo));
				tw.AddTree (headTree);
				tw.AddTree (new DirCacheIterator (_repo.ReadDirCache ()));
				
				while (tw.Next ()) {
					// Ignore untracked files
					if (tw.IsSubtree)
						tw.EnterSubtree ();
					else if (tw.GetFileMode (0) != NGit.FileMode.MISSING && (tw.GetFileMode (1) != NGit.FileMode.MISSING || tw.GetFileMode (2) != NGit.FileMode.MISSING)) {
						WorkingTreeIterator f = tw.GetTree<WorkingTreeIterator>(0);
						DirCacheEntry ce = new DirCacheEntry (tw.PathString);
						long sz = f.GetEntryLength();
						ce.SetLength (sz);
						ce.SetLastModified (f.GetEntryLastModified());
						ce.SetFileMode (f.GetEntryFileMode());
						var data = f.OpenEntryStream();
						try {
							ce.SetObjectId (oi.Insert (Constants.OBJ_BLOB, sz, data));
						} finally {
							data.Close ();
						}
						cb.Add (ce);
					}
				}
				
				cb.Finish ();
				return dc.WriteTree (oi);
			} finally {
				oi.Release ();
			}
		}
		
		internal void Apply (Stash stash)
		{
			ObjectId cid = _repo.Resolve (stash.CommitId);
			RevWalk rw = new RevWalk (_repo);
			RevCommit wip = rw.ParseCommit (cid);
			RevCommit index = wip.Parents.Last ();
//			ObjectId headTree = _repo.Resolve (Constants.HEAD + "^{tree}");
			ObjectId headTree = rw.ParseCommit (wip.Parents.First ()).Tree;
			RevTree wipTree =  wip.Tree;
			
			DirCache dc = _repo.LockDirCache ();
			try {
				DirCacheCheckout co = new DirCacheCheckout (_repo, headTree, dc, wipTree, new FileTreeIterator (_repo));
				co.SetFailOnConflict (false);
				co.Checkout ();
			} catch {
				dc.Unlock ();
				throw;
			}
			
/*			List<DirCacheEntry> toAdd = new List<DirCacheEntry> ();
			
			DirCache dc = DirCache.Lock (_repo._internal_repo);
			try {
				var cacheEditor = dc.editor ();
				
				// The WorkDirCheckout class doesn't check if there are conflicts in modified files,
				// so we have to do it here.
				
				foreach (var c in co.Updated) {
					
					var baseEntry = wip.Parents.First ().Tree[c.Key] as Leaf;
					var oursEntry = wipTree [c.Key] as Leaf;
					var theirsEntry = headTree [c.Key] as Leaf;
					
					if (baseEntry != null && oursEntry != null && currentIndexTree [c.Key] == null) {
						// If a file was reported as updated but that file is not present in the stashed index,
						// it means that the file was scheduled to be deleted.
						cacheEditor.@add (new DirCacheEditor.DeletePath (c.Key));
						File.Delete (_repo.FromGitPath (c.Key));
					}
					else if (baseEntry != null && oursEntry != null && theirsEntry != null) {
						MergeResult res = MergeAlgorithm.merge (new RawText (baseEntry.RawData), new RawText (oursEntry.RawData), new RawText (theirsEntry.RawData));
						MergeFormatter f = new MergeFormatter ();
						using (BinaryWriter bw = new BinaryWriter (File.OpenWrite (_repo.FromGitPath (c.Key)))) {
							f.formatMerge (bw, res, "Base", "Stash", "Head", Constants.CHARSET.WebName);
						}
						if (res.containsConflicts ()) {
							// Remove the entry from the index. It will be added later on.
							cacheEditor.@add (new DirCacheEditor.DeletePath (c.Key));
					
							// Generate index entries for each merge stage
							// Those entries can't be added right now to the index because a DirCacheEditor
							// can't be used at the same time as a DirCacheBuilder.
							var e = new DirCacheEntry(c.Key, DirCacheEntry.STAGE_1);
							e.setObjectId (baseEntry.InternalEntry.Id);
							e.setFileMode (baseEntry.InternalEntry.Mode);
							toAdd.Add (e);
							
							e = new DirCacheEntry(c.Key, DirCacheEntry.STAGE_2);
							e.setObjectId (oursEntry.InternalEntry.Id);
							e.setFileMode (oursEntry.InternalEntry.Mode);
							toAdd.Add (e);
							
							e = new DirCacheEntry(c.Key, DirCacheEntry.STAGE_3);
							e.setObjectId (theirsEntry.InternalEntry.Id);
							e.setFileMode (theirsEntry.InternalEntry.Mode);
							toAdd.Add (e);
						}
					}
				}
				
				cacheEditor.finish ();
				
				if (toAdd.Count > 0) {
					// Add the index entries generated above
					var cacheBuilder = dc.builder ();
					for (int n=0; n<dc.getEntryCount (); n++)
						cacheBuilder.@add (dc.getEntry (n));
					foreach (var entry in toAdd)
						cacheBuilder.@add (entry);
					cacheBuilder.finish ();
				}
				
				dc.write ();
				dc.commit ();
			} catch {
				dc.unlock ();
				throw;
			}*/
		}
		
		public void Remove (Stash s)
		{
			List<Stash> stashes = ReadStashes ();
			Remove (stashes, s);
		}
		
		public void Pop ()
		{
			List<Stash> stashes = ReadStashes ();
			Stash last = stashes.Last ();
			last.Apply ();
			Remove (stashes, last);
		}
		
		public void Clear ()
		{
			if (StashRefFile.Exists)
				StashRefFile.Delete ();
			if (StashLogFile.Exists)
				StashLogFile.Delete ();
		}
		
		void Remove (List<Stash> stashes, Stash s)
		{
			int i = stashes.FindIndex (st => st.CommitId == s.CommitId);
			if (i != -1) {
				stashes.RemoveAt (i);
				if (stashes.Count == 0) {
					// No more stashes. The ref and log files can be deleted.
					StashRefFile.Delete ();
					StashLogFile.Delete ();
					return;
				}
				WriteStashes (stashes);
				if (i == stashes.Count) {
					// We deleted the head. Write the new head.
					File.WriteAllText (StashRefFile.FullName, stashes.Last ().CommitId + "\n");
				}
			}
		}
		
		public IEnumerator<Stash> GetEnumerator ()
		{
			return ReadStashes ().GetEnumerator ();
		}
		
		List<Stash> ReadStashes ()
		{
			// Reads the registered stashes
			// Results are returned from the bottom to the top of the stack
			
			List<Stash> result = new List<Stash> ();
			FileInfo logFile = StashLogFile;
			if (!logFile.Exists)
				return result;
			
			Dictionary<string,Stash> stashes = new Dictionary<string, Stash> ();
			Stash first = null;
			foreach (string line in File.ReadAllLines (logFile.FullName)) {
				Stash s = Stash.Parse (line);
				s.StashCollection = this;
				if (s.PrevStashCommitId == null)
					first = s;
				else
					stashes.Add (s.PrevStashCommitId, s);
			}
			while (first != null) {
				result.Add (first);
				stashes.TryGetValue (first.CommitId, out first);
			}
			return result;
		}
		
		void WriteStashes (List<Stash> list)
		{
			StringBuilder sb = new StringBuilder ();
			foreach (var s in list) {
				sb.Append (s.FullLine);
				sb.Append ('\n');
			}
			File.WriteAllText (StashLogFile.FullName, sb.ToString ());
		}
		
		IEnumerator IEnumerable.GetEnumerator ()
		{
			return GetEnumerator ();
		}
	}
}
