" Vim filetype plugin

if exists('b:did_ftplugin')
    finish
endif
let b:did_ftplugin = 1

python <<EOF
import vim
import os
fsharp_dir = vim.eval("expand('<sfile>:p:h')")
sys.path.append(fsharp_dir)
from fsharpvim import FSAutoComplete,Statics
if(Statics.fsac == None):
    Statics.fsac = FSAutoComplete(fsharp_dir)
fsautocomplete = Statics.fsac
proj_file = None
#find project file if any - assumes fsproj file will be in the same directory as the fs or fsi file
file_name = vim.current.buffer.name
x,ext = os.path.splitext(file_name)
if('.fs' == ext or '.fsi' == ext):
    dir = os.path.dirname(os.path.realpath(file_name))
    projs = filter(lambda f: '.fsproj' == os.path.splitext(f)[1], os.listdir(dir))
    if(len(projs)):
        proj_file = os.path.join(dir, projs[0])
        fsautocomplete.project(proj_file)
        #print "found project file %s" % proj_file
EOF

let s:cpo_save = &cpo
set cpo&vim

setl updatetime=750
" enable syntax based folding
setl fdm=syntax

" comment settings
setl formatoptions=croql
setl commentstring=(*%s*)
setl comments=s0:*\ -,m0:*\ \ ,ex0:*),s1:(*,mb:*,ex:*),:\/\/\/,:\/\/

nnoremap <leader>e :call ShowErrors()<cr>
nnoremap <leader>t :call TypeCheck()<cr>


augroup fsharp
    autocmd!
    "remove scratch buffer after selection
    "autocmd CursorMovedI * if pumvisible() == 0|pclose|endif
    autocmd InsertLeave *.fs* if pumvisible() == 0|pclose|endif
    "parse file when entering Normal mode after an edit.
    "this may not be fast enough for large files.
    autocmd InsertLeave *.fs* call OnInsertLeave() 
    autocmd TextChanged *.fs* call OnTextChanged()
    autocmd TextChangedI *.fs* call OnTextChanged()
    autocmd CursorHold *.fs* call OnCursorHold()
augroup END

com! -buffer -range=% Interactive call s:launchInteractive(<line1>, <line2>)
com! -buffer LogFile call s:printLogFile()
com! -buffer ParseProject call s:parseProject() 
com! -buffer BuildProject call s:buildProject() 

highlight FError gui=undercurl guisp='red'
highlight FWarn gui=undercurl guisp='gray'
highlight FErrSign ctermbg='red' guibg='red'
highlight FWarnSign ctermbg='yellow' guibg='yellow'

sign define fserr text=>> texthl=FErrSign
sign define fswarn text=>> texthl=FWarnSign

" make ftplugin undo-able
let b:undo_ftplugin = 'setl fo< cms< com< fdm<'

let s:candidates = [ 'fsi',
            \ 'fsi.exe',
            \ 'fsharpi',
            \ 'fsharpi.exe' ]

if !exists('g:fsharp_interactive_bin')
    let g:fsharp_interactive_bin = ''
    for c in s:candidates
        if executable(c)
            let g:fsharp_interactive_bin = c
        endif
    endfor
endif

" to avoid re parsing when just moving around in normal mode
let b:shouldParse = 1

function! OnCursorHold ()
    if b:shouldParse 
        call ShowErrors()
        let b:shouldParse = 0
    endif
endfunction

function! OnTextChanged()
    let b:shouldParse = 1
    call TextChange()
endfunction

function! OnInsertLeave()
endfunction

function! s:printLogFile()
python << EOF
print fsautocomplete.logfiledir
EOF
endfunction

function! s:parseProject()
python << EOF
if(proj_file):
    fsautocomplete.project(proj_file)
EOF
endfunction


function! s:buildProject()
    try
        let pn = pyeval('proj_file')
        :execute '!xbuild ' . pn
    catch
        echo "failed to execute build"
    endtry
endfunction

function! s:launchInteractive(from, to)
    if !executable(g:fsharp_interactive_bin)
        echohl WarningMsg
        echom 'fsharp.vim: no fsharp interactive binary found'
        echom 'fsharp.vim: set g:fsharp_interactive_bin appropriately'
        echohl None
        return
    end

    let tmpfile = tempname()
    echo tmpfile
    exec a:from . ',' . a:to . 'w! ' . tmpfile
    exec '!' . g:fsharp_interactive_bin '--gui- --nologo --use:"'.tmpfile.'"'
endfunction

function! TextChange()
    call clearmatches()
endfunction

function! TypeCheck()
python << EOF
b = vim.current.buffer
fsautocomplete.parse(b.name, True, b)
row, col = vim.current.window.cursor
res = fsautocomplete.tooltip(b.name, row, col)
lines = res.splitlines()
first = ""
if(len(lines)):
    first = lines[0]
if(first == 'Multiple items' or first.startswith('type')):
    vim.command('echo "%s"' % res)
else:
    vim.command('echo "%s"' % first)
EOF
endfunction

"probable loclist format
"{'lnum': 2, 'bufnr': 1, 'col': 1, 'valid': 1, 'vcol': 1, 'nr': -1, 'type': 'W', 'pattern': '', 'text': 'Expected an assignment or functi on call and instead saw an expression.'}

"fsautocomplete format
"{"StartLine":4,"StartLineAlternate":5,"EndLine":4,"EndLineAlternate":5,"StartColumn":0,"EndColumn":4,"Severity":"Error","Message":"The value or constructor 'asdf' is not defined","Subcategory":"typecheck","FileName":"/Users/karlnilsson/code/kjnilsson/fsharp-vim/test.fsx"}

function! s:convertToLocList(errs)
    let result = []
    for e in a:errs
        :call add(result, 
                    \{'lnum': e['StartLineAlternate'], 
                    \'col': e['StartColumn'],
                    \'ecol': e['EndColumn'],
                    \'type': e['Severity'][0],
                    \'text': e['Message'],
                    \'pattern': '\%' . e['StartLineAlternate'] . 'l\%>' . e['StartColumn'] .  'c\%<' . (e['EndColumn'] + 1) . 'c',
                    \'valid': 1 })
    endfor
    return result 
endfunction


function! ShowErrors()
    let errs = pyeval('fsautocomplete.errors(vim.current.buffer.name, True, vim.current.buffer)')
    let lerrs = s:convertToLocList(errs)
    call setloclist(0, lerrs)
    execute "sign unplace *"
    call clearmatches()
    for e in lerrs
        "place signs
        if e['type'] == "E"
            execute "sign place 1 line=" . e['lnum'] . " name=fserr file=" . expand("%:p")
            call matchadd('FError', e['pattern'])
        else
            execute "sign place 1 line=" . e['lnum'] . " name=fswarn file=" . expand("%:p")
            call matchadd('FWarn', e['pattern'])
        endif
    endfor
endfunction

function! fsharp#Balloon()
python << EOF
b = vim.current.buffer
fsautocomplete.parse(b.name, True, b)
res = fsautocomplete.tooltip(b.name, int(vim.eval('v:beval_lnum')), int(vim.eval('v:beval_col')))
if(res != None or res != ""):
    vim.command('return "%s"' % res) 
else:
    print "failed to get ballon tip"
EOF
endfunction

function! fsharp#Complete(findstart, base)
    let line = getline('.')
    let idx = col('.')
    while idx > 0
        let c = line[idx]
        if c == ' ' || c == '.'
            let idx += 1
            break
        endif
        let idx -= 1
    endwhile

    if a:findstart == 1
        return idx
    else
python << EOF
b = vim.current.buffer
row, col = vim.current.window.cursor
line = b[row - 1]
if col > len(line):
    col = len(line)
fsautocomplete.parse(b.name, True, b)
vim.command('return %s' % fsautocomplete.complete(b.name, row, col, vim.eval('a:base')))
EOF
    endif
endfunction

let &cpo = s:cpo_save

" vim: sw=4 et sts=4
