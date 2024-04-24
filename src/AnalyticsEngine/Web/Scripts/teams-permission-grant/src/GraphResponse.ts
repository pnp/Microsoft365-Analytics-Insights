


 export interface GraphResponse<Type> {
    (arg: Type): Type;
    value : Array<Type>;
  }