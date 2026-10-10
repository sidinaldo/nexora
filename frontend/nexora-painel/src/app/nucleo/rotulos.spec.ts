import { rotuloOrigem, rotuloPapel, rotuloQualidadeMeta, rotuloStatusLembrete } from './rotulos';

/** Os códigos que vinham crus na tela (BUG-XX): "indicacao", "dono", "concluido", "GREEN". */
describe('rótulos da tela', () => {
  it('a origem sai na língua de quem lê, e a desconhecida sai crua', () => {
    expect(rotuloOrigem('indicacao')).toBe('Indicação');
    expect(rotuloOrigem('meta_ads')).toBe('Meta Ads');
    expect(rotuloOrigem('origem_nova')).toBe('origem_nova');
    expect(rotuloOrigem(null)).toBe('');
  });

  it('o papel começa com maiúscula', () => {
    expect(rotuloPapel('dono')).toBe('Dono');
    expect(rotuloPapel('gestor')).toBe('Gestor');
    expect(rotuloPapel('vendedor')).toBe('Vendedor');
  });

  it('o lembrete resolvido diz como terminou', () => {
    expect(rotuloStatusLembrete('concluido')).toBe('Concluído');
    expect(rotuloStatusLembrete('cancelado')).toBe('Cancelado');
  });

  it('a qualidade da Meta vira palavra', () => {
    expect(rotuloQualidadeMeta('GREEN')).toBe('boa');
    expect(rotuloQualidadeMeta('YELLOW')).toBe('média');
    expect(rotuloQualidadeMeta('RED')).toBe('baixa');
    expect(rotuloQualidadeMeta('UNKNOWN')).toBe('ainda sem avaliação');
  });
});
