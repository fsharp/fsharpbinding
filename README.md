# F# Language Support for Open Editors

[![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/fsharp/fsharpbinding?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

This project contains advanced editing support for F# for a number of open editors. It is made up of the following projects:
* [F# mode for Emacs](emacs/README.md)
* [F# mode for Vim](vim/README.mkd)
* [F# mode for Sublime Text](sublimetext/README.md)
* [FSharp.AutoComplete](FSharp.AutoComplete/README.md)
* An old copy of the [F# addin for MonoDevelop and Xamarin Studio 5.0](monodevelop/README.md).  The latest development branch of this code is now hosted at [FSharpMDXS](https://github.com/fsharp/FSharpMDXS)


If you are interested in adding rich editor support for another editor, please open an [issue](https://github.com/fsharp/fsharpbinding/issues) to kick-start the discussion.

See the [F# Cross-Platform Development Guide](http://fsharp.org/guides/mac-linux-cross-platform/index.html#editing) for F# with Sublime Text 2, Vim and other editors not covered here.

## Build Status

The CI builds are handled by a [FAKE script](FSharp.AutoComplete/build.fsx), which:

* Builds FSharp.AutoComplete
* Runs FSharp.AutoComplete unit tests
* Runs FSharp.AutoComplete integration tests
* Runs Emacs unit tests
* Runs Emacs integration tests
* Runs Emacs byte compilation

### Travis [![Travis build status](https://travis-ci.org/fsharp/fsharpbinding.png)](https://travis-ci.org/fsharp/fsharpbinding)

See [.travis.yml](.travis.yml) for details.

### AppVeyor [![AppVeyor build status](https://ci.appveyor.com/api/projects/status/y1s7nje31qi1j8ed)](https://ci.appveyor.com/project/fsgit/fsharpbinding)

The configuration is contained in [appveyor.yml](appveyor.yml). Currently the emacs integration tests do not run successfully on AppVeyor and are excluded by the FAKE script.

## Building, using and contributing

See the README for each individual component:

* [fsautocomplete](FSharp.AutoComplete/README.md)
* [emacs](emacs/README.md)
* [vim](vim/README.mkd)
* [Sublime Text](sublimetext/README.md)

## Shared Components

The core shared component is FSharp.Compiler.Service.dll from the 
community [FSharp.Compiler.Service](https://github.com/fsharp/FSharp.Compiler.Service) project.
This is used by both [fsautocomplete.exe](https://github.com/fsharp/fsharpbinding/tree/master/FSharp.AutoComplete), 
a command-line utility to sit behind Emacs, Vim and other editing environments components. 

For more information about F# see [The F# Software Foundation](http://fsharp.org). Join [The F# Open Source Group](http://fsharp.github.io). We use [github](https://github.com/fsharp/fsharpbinding) for tracking work items and suggestions.
