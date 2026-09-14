package extension.bridge

import extension.bridge.ChildFirstURLClassLoader
import extension.bridge.logging.androidCompatLogger
import java.net.URL
import java.net.URLClassLoader
import kotlin.io.path.Path
import eu.kanade.tachiyomi.source.CatalogueSource
import eu.kanade.tachiyomi.source.Source
import eu.kanade.tachiyomi.source.SourceFactory

object Extensions {
    private val logger = androidCompatLogger(Extensions::class.java)

    /**
     * Loads the extension main class called [className] from the jar located at [jarPath]
     * and returns all sources it provides.
     */
    fun loadExtensionSources(
        jarPath: String,
        className: String,
    ): List<CatalogueSource> {
        val extensionMainClassInstance = loadExtension(jarPath, className)
        val sources: List<CatalogueSource> =
            when (extensionMainClassInstance) {
                is Source -> listOf(extensionMainClassInstance)
                is SourceFactory -> extensionMainClassInstance.createSources()
                else -> throw RuntimeException("Unknown source class type! ${extensionMainClassInstance.javaClass}")
            }.map { it as CatalogueSource }
        return sources
    }

    /**
     * Loads the extension main class called [className] from the jar located at [jarPath].
     * It may return an instance of HttpSource or SourceFactory depending on the extension.
     *
     * The class loader is cached in the shared [jarLoaderMap] so that repeated loads of the
     * same jar reuse the loader (classes are singletons per loader) and so that
     * [unloadExtension] can close it. A fresh loader is never left orphaned.
     */
    fun loadExtension(
        jarPath: String,
        className: String,
    ): Any {
        try {
            val classLoader = loadOrCreateLoader(jarPath)
            val classToLoad = Class.forName(className, false, classLoader)
            return classToLoad.getDeclaredConstructor().newInstance()
        } catch (e: Exception) {

            throw e
        }
    }

    /**
     * Returns the cached class loader for [jarPath] or creates, registers and returns one.
     *
     * Uses [ChildFirstURLClassLoader] with NO explicit parent (null → system/boot loader),
     * which mirrors the original, working load path. The child-first strategy tries the
     * extension jar's own classes first, so jar-local types (e.g. the keiyoushi
     * `source.Generated` entry class) resolve even though the parent IKVM RuntimeClassLoader
     * cannot see them. Passing an explicit parent (e.g. the AndroidCompat runtime loader)
     * made the parent take over resolution of extension-only classes and broke loading.
     */
    private fun loadOrCreateLoader(jarPath: String): URLClassLoader {
        synchronized(jarLoaderMap) {
            return jarLoaderMap[jarPath] ?: ChildFirstURLClassLoader(
                arrayOf<URL>(Path(jarPath).toUri().toURL()),
            ).also { jarLoaderMap[jarPath] = it }
        }
    }
}
