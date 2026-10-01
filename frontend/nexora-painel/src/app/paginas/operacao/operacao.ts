import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ChaveOperador } from '../../nucleo/seguranca/chave-operador';
import { OperacaoEmpresas } from './empresas/empresas';
import { OperacaoPlanos } from './planos/planos';

export type AbaOperacao = 'empresas' | 'planos';

/** ÁREA DO OPERADOR — a sua, não a do cliente (OPE-1).
 *
 *  ===================== POR QUE ELA NÃO TEM LOGIN =====================
 *  Não há usuário aqui, e não há papel: a credencial é a chave de administração, a mesma que cria
 *  empresa. Um "usuário super" dentro do produto significaria um login que atravessa empresas — e é
 *  exatamente isso que os 26 filtros de isolamento existem para impedir.
 *
 *  Identidade e revogação por pessoa vêm do Cloudflare Access, na frente destas rotas: ele diz
 *  QUEM abriu, e tirar o acesso de alguém não exige trocar o segredo dos outros.
 *  ====================================================================
 *
 *  ===================== A CHAVE DURA O TEMPO DA ABA =====================
 *  A tela de criar empresa pede a chave, usa e esquece — um gesto. Aqui há lista, detalhe e
 *  catálogo, e pedir a chave a cada navegação transformaria a área em algo que ninguém usa.
 *
 *  Ela vive num signal, em memória. Recarregar a página pede de novo, e isso é o desejado — não um
 *  esquecimento a corrigir. Ver `ChaveOperador`.
 *  ======================================================================
 *
 *  ⚠️ NÃO HÁ LINK PARA CÁ em tela nenhuma, e o motivo não é segurança — quem barra é a chave. É que
 *  nenhum cliente do produto precisa encontrar uma tela chamada "operação" e perguntar o que é. */
@Component({
  selector: 'app-operacao',
  imports: [FormsModule, RouterLink, OperacaoEmpresas, OperacaoPlanos],
  templateUrl: './operacao.html',
  styleUrl: './operacao.css'
})
export class Operacao {
  chave = inject(ChaveOperador);

  digitada = signal('');
  aba = signal<AbaOperacao>('empresas');

  destravar() {
    const valor = this.digitada().trim();
    if (!valor) return;
    this.chave.definir(valor);
    this.digitada.set('');
  }

  /** Sair da área esquece a chave. Mantê-la depois de "sair" faria o botão não fazer nada do que
   *  ele promete. */
  sair() {
    this.chave.limpar();
    this.digitada.set('');
  }
}
