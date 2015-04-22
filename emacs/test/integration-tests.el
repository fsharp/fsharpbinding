(require 'ert)

(defun wait-for-condition (fun)
  "Wait up to 5 seconds for (fun) to return non-nil"
  (with-timeout (10)
    (while (not (funcall fun))
      (sleep-for 1))))

(defun fsharp-mode-wrapper (bufs body)
  "Load fsharp-mode and make sure any completion process is killed after test"
  (unwind-protect
      ; Run the actual test
      (funcall body)

    ; Clean up below

    ; Close any buffer requested by the test
    (dolist (buf bufs)
      (when (get-buffer buf)
        (switch-to-buffer buf)
        (when (file-exists-p buffer-file-name)
          (revert-buffer t t))
        (kill-buffer buf)))

    ; Close any buffer associated with the loaded project
    (mapc (lambda (buf)
            (when (gethash (buffer-file-name buf) fsharp-ac--project-files)
              (switch-to-buffer buf)
              (revert-buffer t t)
              (kill-buffer buf)))
          (buffer-list))

    ; Stop the fsautocomplete process and close its buffer
    (fsharp-ac/stop-process)
    (wait-for-condition (lambda () (not (fsharp-ac--process-live-p))))
    (when (fsharp-ac--process-live-p)
      (kill-process fsharp-ac-completion-process)
      (wait-for-condition (lambda () (not (fsharp-ac--process-live-p)))))
    (when (get-buffer "*fsharp-complete*")
      (kill-buffer "*fsharp-complete*"))

    ; Kill the FSI process and buffer, if it was used
    (let ((inf-fsharp-process (get-process inferior-fsharp-buffer-subname)))
      (when inf-fsharp-process
        (when (process-live-p inf-fsharp-process)
          (kill-process inf-fsharp-process)
          (wait-for-condition (lambda () (not (process-live-p
                                          inf-fsharp-process)))))))))

(defun find-file-and-wait-for-project-load (file)
  (find-file file)
  (wait-for-condition (lambda () (and (gethash (fsharp-ac--buffer-truename) fsharp-ac--project-files)
                                 (/= 0 (hash-table-count fsharp-ac--project-data))))))

(ert-deftest check-project-files ()
  "Check the program files are set correctly"
  (fsharp-mode-wrapper '("Program.fs")
   (lambda ()
     (find-file-and-wait-for-project-load "test/Test1/Program.fs")
     (let ((project (gethash (fsharp-ac--buffer-truename) fsharp-ac--project-files))
           (projectfiles))
       (maphash (lambda (k _) (add-to-list 'projectfiles k)) fsharp-ac--project-files)
       (should-match "Test1/Program.fs" (s-join "" projectfiles))
       (should-match "Test1/FileTwo.fs" (s-join "" projectfiles))
       (should-match "Test1/bin/Debug/Test1.exe"
                     (gethash "Output" (gethash project fsharp-ac--project-data)))))))

(ert-deftest check-completion ()
  "Check completion-at-point works"
  (fsharp-mode-wrapper '("Program.fs")
   (lambda ()
     (find-file-and-wait-for-project-load "test/Test1/Program.fs")
     (search-forward "X.func")
     (delete-backward-char 2)
     (auto-complete)
     (ac-complete)
     (beginning-of-line)
     (should (search-forward "X.func")))))

(ert-deftest check-gotodefn ()
  "Check jump to (and back from) definition works"
  (fsharp-mode-wrapper '("Program.fs")
   (lambda ()
     (find-file-and-wait-for-project-load "test/Test1/Program.fs")
     (search-forward "X.func")
     (backward-char 2)
     (fsharp-ac-parse-current-buffer t)
     (fsharp-ac/gotodefn-at-point)
     (wait-for-condition (lambda () (/= (point) 88)))
     (should= (point) 18)
     (fsharp-ac/pop-gotodefn-stack)
     (should= (point) 88))))

(ert-deftest check-tooltip ()
  "Check tooltip request works"
  (fsharp-mode-wrapper '("Program.fs")
   (lambda ()
     (let ((tiptext)
           (fsharp-ac-use-popup t))
       (noflet ((fsharp-ac/show-popup (s) (setq tiptext s)))
         (find-file-and-wait-for-project-load "test/Test1/Program.fs")
         (search-forward "X.func")
         (backward-char 2)
         (fsharp-ac-parse-current-buffer t)
         (fsharp-ac/show-tooltip-at-point)
         (wait-for-condition (lambda () tiptext))
         (should-match "val func : x:int -> int\n\nFull name: Program.X.func"
                       tiptext))))))

(ert-deftest check-errors ()
  "Check error underlining works"
  (fsharp-mode-wrapper '("Program.fs")
   (lambda ()
     (find-file-and-wait-for-project-load "test/Test1/Program.fs")
     (search-forward "X.func")
     (delete-backward-char 1)
     (backward-char)
     (fsharp-ac-parse-current-buffer t)
     (wait-for-condition (lambda () (> (length (overlays-at (point))) 0)))
     (should= (overlay-get (car (overlays-at (point))) 'face)
              'fsharp-error-face)
     (should= (overlay-get (car (overlays-at (point))) 'help-echo)
              "Unexpected keyword 'fun' in binding. Expected incomplete structured construct at or before this point or other token."))))

(ert-deftest check-script-tooltip ()
  "Check we can request a tooltip from a script"
  (fsharp-mode-wrapper '("Script.fsx")
   (lambda ()
     (let ((tiptext)
           (fsharp-ac-use-popup t))
       (noflet ((fsharp-ac/show-popup (s) (setq tiptext s)))
         (find-file-and-wait-for-project-load "test/Test1/Script.fsx")
         (fsharp-ac-parse-current-buffer t)
         (search-forward "XA.fun")
         (fsharp-ac/show-tooltip-at-point)
         (wait-for-condition (lambda () tiptext))
         (should-match "val funky : x:int -> int\n\nFull name: Script.XA.funky"
                       tiptext))))))

(ert-deftest check-inf-fsharp ()
  "Check that FSI can be used to evaluate"
  (fsharp-mode-wrapper '("tmp.fsx")
   (lambda ()
     (fsharp-run-process-if-needed inferior-fsharp-program)
     (wait-for-condition (lambda () (get-buffer inferior-fsharp-buffer-name)))
     (find-file "tmp.fsx")
     (goto-char (point-max))
     (insert "let myvariable = 123 + 456")
     (fsharp-eval-phrase)
     (switch-to-buffer inferior-fsharp-buffer-name)
     (wait-for-condition (lambda () (search-backward "579" nil t)))
     (should-match "579" (buffer-substring-no-properties (point-min) (point-max))))))

(ert-deftest check-multi-project ()
  "Check that we can get intellisense for multiple projects at once"
  (fsharp-mode-wrapper '("FileTwo.fs" "Main.fs")
   (lambda ()
     (find-file-and-wait-for-project-load "test/Test1/FileTwo.fs")
     (find-file-and-wait-for-project-load "../Test2/Main.fs")

     (switch-to-buffer "FileTwo.fs")
     (search-forward "   y")
     (fsharp-ac-parse-current-buffer t)
     (fsharp-ac/gotodefn-at-point)
     (wait-for-condition (lambda () (/= (point) 159)))
     (should= (point) 137)

     (switch-to-buffer "Main.fs")
     (search-forward "\" val2")
     (fsharp-ac-parse-current-buffer t)
     (fsharp-ac/gotodefn-at-point)
     (wait-for-condition (lambda () (/= (point) 113)))
     (should= (point) 24))))
