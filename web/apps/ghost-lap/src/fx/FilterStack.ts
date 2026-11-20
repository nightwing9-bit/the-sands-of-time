// The ONE owner of the world container's `filters` list. Burst distortion and CRT interference both need a filter on the same game-view
// container (never the DOM, never the HUD), so neither writes `container.filters` itself: they add and remove their filter here.
//
// While the list is empty the container has NO filter and NO filterArea, so an idle arena pays no offscreen pass. The filter area is the
// world rectangle in the container's own units (1600x900), which Pixi transforms to screen pixels; that is what lets an effect convert a
// world position to filter pixels with one multiplication by the world scale.
import { Rectangle, type Container, type Filter } from "pixi.js";

export class FilterStack {
  private readonly filters: Filter[] = [];
  private readonly area: Rectangle;
  constructor(private readonly target: Container, worldWidth: number, worldHeight: number) {
    this.area = new Rectangle(0, 0, worldWidth, worldHeight);
  }
  get count(): number { return this.filters.length; }
  has(filter: Filter): boolean { return this.filters.includes(filter); }
  add(filter: Filter): void {
    if (this.filters.includes(filter)) return;
    this.filters.push(filter);
    this.apply();
  }
  remove(filter: Filter): void {
    const i = this.filters.indexOf(filter);
    if (i < 0) return;
    this.filters.splice(i, 1);
    this.apply();
  }
  clear(): void {
    this.filters.length = 0;
    this.apply();
  }
  private apply(): void {
    if (this.filters.length === 0) {
      this.target.filters = null;
      this.target.filterArea = undefined;
    } else {
      this.target.filters = [...this.filters];
      this.target.filterArea = this.area;
    }
  }
}
