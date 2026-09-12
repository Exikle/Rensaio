"use client";

import React, { useState, useRef, useEffect } from 'react';

interface LazyImageProps extends React.ImgHTMLAttributes<HTMLImageElement> {
  src: string;
  alt: string;
  fallbackSrc?: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  threshold?: number;
}

export function LazyImage({ 
  src, 
  alt, 
  fallbackSrc = '/rensaio.png',
  className = '',
  loading = 'lazy',
  threshold = 0.1,
  ...props 
}: LazyImageProps) {
  const [imageSrc, setImageSrc] = useState<string | null>(null);
  const [isLoaded, setIsLoaded] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const observerRef = useRef<IntersectionObserver | null>(null);
  const loadStartedRef = useRef(false);

  // Reset internal state whenever a new src is requested.
  useEffect(() => {
    setImageSrc(null);
    setIsLoaded(false);
    loadStartedRef.current = false;
  }, [src]);

  // Decide *once* whether to load — keyed only on src/loading/threshold so that
  // the fallback swap in handleError can never re-trigger loading of the original src.
  useEffect(() => {
    if (loading === 'eager' || typeof window === 'undefined' || !('IntersectionObserver' in window)) {
      setImageSrc(src);
      loadStartedRef.current = true;
      return;
    }

    if (loadStartedRef.current) return;

    observerRef.current = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            setImageSrc(src);
            loadStartedRef.current = true;
            // Disconnect observer after loading starts
            if (observerRef.current && containerRef.current) {
              observerRef.current.unobserve(containerRef.current);
            }
          }
        });
      },
      {
        threshold,
        rootMargin: '50px', // Start loading 50px before the image enters viewport
      }
    );

    if (containerRef.current) {
      observerRef.current.observe(containerRef.current);
    }

    return () => {
      if (observerRef.current) {
        observerRef.current.disconnect();
      }
    };
  }, [src, loading, threshold]);

  const handleLoad = () => {
    setIsLoaded(true);
  };

  const handleError = () => {
    setIsLoaded(true);
    // Degrade to the fallback exactly once; never revert back to the original src.
    setImageSrc((current) => (current === src ? fallbackSrc : current));
  };

  return (
    <div ref={containerRef} className={`relative overflow-hidden ${className}`}>
      {/* Placeholder while loading */}
      {!isLoaded && (
        <div className={`absolute inset-0 bg-muted animate-pulse rounded-lg`} />
      )}
      
      {/* Actual image */}
      {imageSrc && (
        <img
          src={imageSrc}
          alt={alt}
          className={`transition-opacity duration-200 ${
            isLoaded ? 'opacity-100' : 'opacity-0'
          } ${className}`}
          onLoad={handleLoad}
          onError={handleError}
          {...props}
        />
      )}
    </div>
  );
}
